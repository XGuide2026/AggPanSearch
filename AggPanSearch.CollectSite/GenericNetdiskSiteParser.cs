using AggPanSearch.CollectSite.NetDisk;
using HtmlAgilityPack;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 通用兜底解析器：不针对具体站点，直接从页面中提取网盘链接，
/// 标题取 h1/title，分类取面包屑或 article:section，简介取 meta description，图片取 og:image。
/// 用于尚未适配专用解析器的资源站。
/// <para>全部 HTML 结构提取均用 HtmlAgilityPack DOM/XPath 实现，不使用正则解析网页。</para>
/// </summary>
public sealed class GenericNetdiskSiteParser : ISiteCollectParser
{
    public string Name => "generic";

    public int Priority => 1000;

    public bool CanHandle(Uri pageUri) => true;

    // 面包屑容器：原正则匹配 div/nav/ol/ul/span/p 且 class 含 breadcrumb 系关键词
    private const string BreadcrumbXPath =
        "//*[self::div or self::nav or self::ol or self::ul or self::span or self::p]" +
        "[contains(@class,'breadcrumb') or contains(@class,'bread-crumb') or contains(@class,'crumb')" +
        " or contains(@class,'position') or contains(@class,'location')" +
        " or contains(@class,'nav-path') or contains(@class,'site-map')]";

    public Task<SiteCollectParseResult> ParseAsync(string html, Uri pageUri, CancellationToken cancellationToken = default)
    {
        SiteCollectParseResult result = new SiteCollectParseResult();
        if (string.IsNullOrWhiteSpace(html))
        {
            result.Status = SiteCollectParseStatus.StructureChanged;
            result.Message = "页面内容为空";
            return Task.FromResult(result);
        }

        HtmlDocument doc = SiteCollectHtmlUtil.LoadDocument(html);
        result.Nav = ParseNavFromDoc(doc, pageUri);

        string plainText = SiteCollectHtmlUtil.GetPlainText(doc);
        List<Candidate> links = NetDiskDetector.ExtractNetDiskLinks(plainText);
        if (links.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "页面中未识别到网盘链接（可能是需要登录或依赖 JS 渲染的页面）";
            return Task.FromResult(result);
        }

        string name = ExtractTitle(doc);
        string intro = ExtractDescription(doc, plainText);
        string category = ExtractCategory(doc, pageUri);
        List<string> images = ExtractImages(doc, pageUri);

        foreach (Candidate link in links)
        {
            CollectedItem item = new CollectedItem
            {
                Name = name,
                Intro = intro,
                Url = link.Url,
                Password = NetDiskDetector.ExtractPassword(plainText, link.Url),
                Category = category,
                DetailUrl = pageUri.AbsoluteUri,
                CloudType = link.Type,
            };
            item.SetImages(images);
            result.Items.Add(item);
        }

        result.Status = SiteCollectParseStatus.Ok;
        return Task.FromResult(result);
    }

    /// <summary>通用「下一页」提取：rel="next" 或链接文本为「下一页 / 下页」。</summary>
    public SiteCollectNavResult ParseNav(string html, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SiteCollectNavResult();
        }
        return ParseNavFromDoc(SiteCollectHtmlUtil.LoadDocument(html), pageUri);
    }

    private static SiteCollectNavResult ParseNavFromDoc(HtmlDocument doc, Uri pageUri)
    {
        SiteCollectNavResult nav = new SiteCollectNavResult();
        HtmlNodeCollection? anchors = doc.DocumentNode.SelectNodes("//a");
        if (anchors == null)
        {
            return nav;
        }

        foreach (HtmlNode anchor in anchors)
        {
            string text = SiteCollectHtmlUtil.CleanText(anchor.InnerText);
            string rel = anchor.GetAttributeValue("rel", string.Empty);
            if (text != "下一页" && text != "下页" &&
                !string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string url = SiteCollectHtmlUtil.ResolveUrl(pageUri, anchor.GetAttributeValue("href", string.Empty));
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                continue;
            }
            if (!uri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(
                    uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    pageUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nav.NextPageUrl = url;
            break;
        }

        return nav;
    }

    private static string ExtractTitle(HtmlDocument doc)
    {
        string h1 = ReadFirstText(doc, "//h1");
        if (h1.Length > 0)
        {
            return h1;
        }

        string title = ReadFirstText(doc, "//title");
        if (title.Length > 0)
        {
            // 去掉常见的「_站点名」「- 站点名」后缀
            int index = title.IndexOf('_');
            if (index > 1)
            {
                title = title.Substring(0, index).Trim();
            }
        }
        return title;
    }

    private static string ExtractDescription(HtmlDocument doc, string plainText)
    {
        string meta = FindMetaContent(doc, "name", "description");
        if (meta.Length > 0)
        {
            return SiteCollectHtmlUtil.Truncate(meta, 500);
        }
        return SiteCollectHtmlUtil.Truncate(plainText, 300);
    }

    private static string ExtractCategory(HtmlDocument doc, Uri pageUri)
    {
        string section = FindMetaContent(doc, "property", "article:section");
        if (section.Length > 0)
        {
            string cleaned = SiteCollectHtmlUtil.CleanCategoryText(section);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        string breadcrumb = ReadFirstText(doc, BreadcrumbXPath);
        if (breadcrumb.Length > 0)
        {
            string cleaned = SiteCollectHtmlUtil.CleanCategoryText(breadcrumb);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        // 兜底：URL 路径首段（跳过纯数字这种无意义的栏目 id）
        string path = pageUri.AbsolutePath.Trim('/');
        if (path.Length > 0)
        {
            string first = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (first.Length > 0 && !first.All(char.IsDigit) && first.Length <= 20)
            {
                return SiteCollectHtmlUtil.CleanCategoryText(Uri.UnescapeDataString(first));
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 详情页多图：og:image 优先（同样过滤主题 logo），其次按页面顺序提取全部可用图片，
    /// 去重后最多 5 张。
    /// </summary>
    private static List<string> ExtractImages(HtmlDocument doc, Uri pageUri)
    {
        List<string> images = new List<string>();
        string ogImage = SiteCollectHtmlUtil.ExtractOgImage(doc, pageUri);
        if (ogImage.Length > 0)
        {
            images.Add(ogImage);
        }
        images.AddRange(SiteCollectHtmlUtil.ExtractImages(doc, pageUri));
        return images
            .Where(SiteCollectHtmlUtil.IsUsableImageUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SiteCollectHtmlUtil.MaxImageCount)
            .ToList();
    }

    /// <summary>取 XPath 命中的第一个非空纯文本（h1/title/面包屑共用）。</summary>
    private static string ReadFirstText(HtmlDocument doc, string xpath)
    {
        HtmlNode? node = doc.DocumentNode.SelectSingleNode(xpath);
        return node == null ? string.Empty : SiteCollectHtmlUtil.CleanText(node.InnerText);
    }

    /// <summary>在 meta 节点中按「属性名=关键词」取 content 值（属性值忽略大小写，等价原忽略大小写正则）。</summary>
    private static string FindMetaContent(HtmlDocument doc, string attrName, string keyword)
    {
        HtmlNodeCollection? metas = doc.DocumentNode.SelectNodes("//meta");
        if (metas == null)
        {
            return string.Empty;
        }
        foreach (HtmlNode meta in metas)
        {
            string name = meta.GetAttributeValue(attrName, string.Empty);
            if (!string.Equals(name, keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string content = SiteCollectHtmlUtil.CleanText(meta.GetAttributeValue("content", string.Empty));
            if (content.Length > 0)
            {
                return content;
            }
        }
        return string.Empty;
    }
}