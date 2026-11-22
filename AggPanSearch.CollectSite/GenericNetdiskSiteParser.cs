using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 通用兜底解析器：不针对具体站点，直接从页面中提取网盘链接，
/// 标题取 h1/title，分类取面包屑或 article:section，简介取 meta description，图片取 og:image。
/// 用于尚未适配专用解析器的资源站。
/// </summary>
public sealed class GenericNetdiskSiteParser : ISiteCollectParser
{
    public string Name => "generic";

    public int Priority => 1000;

    public bool CanHandle(Uri pageUri) => true;

    private static readonly Regex TitlePattern = RegexCache.Get(
        @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex H1Pattern = RegexCache.Get(
        @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MetaDescriptionPattern = RegexCache.Get(
        @"<meta[^>]+name=[""']description[""'][^>]*content=[""'](?<c>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MetaSectionPattern = RegexCache.Get(
        @"<meta[^>]+property=[""']article:section[""'][^>]*content=[""'](?<c>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex BreadcrumbPattern = RegexCache.Get(
        @"<(?:div|nav|ol|ul|span|p)\b[^>]+class=[""'][^""']*(?:breadcrumb|bread-crumb|crumb|position|location|nav-path|site-map)[^""']*[""'][^>]*>(?<b>.*?)</(?:div|nav|ol|ul|span|p)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex AnchorPattern = RegexCache.Get(
        @"<a\b(?<attrs>[^>]{0,2000})>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HrefPattern = RegexCache.GetIgnoreCase(@"\bhref=[""'](?<h>[^""']+)[""']");

    private static readonly Regex RelNextPattern = RegexCache.GetIgnoreCase(@"\brel=[""']next[""']");

    public Task<SiteCollectParseResult> ParseAsync(string html, Uri pageUri, CancellationToken cancellationToken = default)
    {
        SiteCollectParseResult result = new SiteCollectParseResult();
        if (string.IsNullOrWhiteSpace(html))
        {
            result.Status = SiteCollectParseStatus.StructureChanged;
            result.Message = "页面内容为空";
            return Task.FromResult(result);
        }
        result.Nav = ParseNav(html, pageUri);

        string plainText = SiteCollectHtmlUtil.ToPlainText(html);
        List<Candidate> links = NetDiskDetector.ExtractNetDiskLinks(plainText);
        if (links.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "页面中未识别到网盘链接（可能是需要登录或依赖 JS 渲染的页面）";
            return Task.FromResult(result);
        }

        string name = ExtractTitle(html);
        string intro = ExtractDescription(html, plainText);
        string category = ExtractCategory(html, pageUri);
        List<string> images = ExtractImages(html, pageUri);

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
        SiteCollectNavResult nav = new SiteCollectNavResult();
        if (string.IsNullOrWhiteSpace(html))
        {
            return nav;
        }

        foreach (Match anchor in AnchorPattern.Matches(html))
        {
            string attrs = anchor.Groups["attrs"].Value;
            string text = SiteCollectHtmlUtil.CleanText(
                SiteCollectHtmlUtil.TagPatternForText(anchor.Groups["text"].Value));
            if (text != "下一页" && text != "下页" && !RelNextPattern.IsMatch(attrs))
            {
                continue;
            }

            string url = SiteCollectHtmlUtil.ResolveUrl(pageUri, HrefPattern.Match(attrs).Groups["h"].Value);
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

    private static string ExtractTitle(string html)
    {
        Match h1 = H1Pattern.Match(html);
        if (h1.Success)
        {
            string text = SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(h1.Groups["t"].Value));
            if (text.Length > 0)
            {
                return text;
            }
        }
        Match title = TitlePattern.Match(html);
        if (title.Success)
        {
            string text = SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(title.Groups["t"].Value));
            // 去掉常见的「_站点名」「- 站点名」后缀
            int index = text.IndexOf('_');
            if (index > 1)
            {
                text = text.Substring(0, index).Trim();
            }
            return text;
        }
        return string.Empty;
    }

    private static string ExtractDescription(string html, string plainText)
    {
        Match meta = MetaDescriptionPattern.Match(html);
        if (meta.Success)
        {
            string text = SiteCollectHtmlUtil.CleanText(meta.Groups["c"].Value);
            if (text.Length > 0)
            {
                return SiteCollectHtmlUtil.Truncate(text, 500);
            }
        }
        return SiteCollectHtmlUtil.Truncate(plainText, 300);
    }

    private static string ExtractCategory(string html, Uri pageUri)
    {
        Match meta = MetaSectionPattern.Match(html);
        if (meta.Success)
        {
            string section = SiteCollectHtmlUtil.CleanCategoryText(meta.Groups["c"].Value);
            if (section.Length > 0)
            {
                return section;
            }
        }

        Match breadcrumb = BreadcrumbPattern.Match(html);
        if (breadcrumb.Success)
        {
            string text = SiteCollectHtmlUtil.CleanCategoryText(
                SiteCollectHtmlUtil.TagPatternForText(breadcrumb.Groups["b"].Value));
            if (text.Length > 0)
            {
                return text;
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
    private static List<string> ExtractImages(string html, Uri pageUri)
    {
        List<string> images = new List<string>();
        string ogImage = SiteCollectHtmlUtil.ExtractOgImage(html, pageUri);
        if (ogImage.Length > 0)
        {
            images.Add(ogImage);
        }
        images.AddRange(SiteCollectHtmlUtil.ExtractImages(html, pageUri));
        return images
            .Where(SiteCollectHtmlUtil.IsUsableImageUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SiteCollectHtmlUtil.MaxImageCount)
            .ToList();
    }
}
