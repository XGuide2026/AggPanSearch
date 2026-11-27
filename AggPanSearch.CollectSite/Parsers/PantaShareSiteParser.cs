using AggPanSearch.CollectSite.NetDisk;
using HtmlAgilityPack;

namespace AggPanSearch.CollectSite.Parsers;

/// <summary>
/// 盘它分享（https://www.pantashare.com/，Astro 静态站）采集解析器。
/// <para>列表页（首页 / /page/N）每张 post-card 卡片已包含完整字段：
/// 标题、分类、简介（可缺）、封面图与全部网盘链接（a.disk-link），无需跳转详情页；
/// 一个卡片含多个网盘链接时按「每条记录一根链接」拆成多条。</para>
/// <para>详情页（/resource/N）走单篇解析（标题 + 分类 + 封面 + 盘链接）；
/// 其他新结构页面返回无卡片时自动由 <see cref="GenericNetdiskSiteParser"/> 兜底。</para>
/// </summary>
public sealed class PantaShareSiteParser : ISiteCollectParser
{
    private const string SiteHost = "pantashare.com";

    /// <summary>单页卡片上限，防止脏页面无限刷。</summary>
    private const int MaxListItems = 200;

    /// <summary>简介入库长度上限。</summary>
    private const int IntroMaxLength = 500;

    public string Name => "pantashare";

    public int Priority => 10;

    public bool CanHandle(Uri pageUri)
    {
        return pageUri.Host.Contains(SiteHost, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- XPath 选择器 ----------

    /// <summary>列表页资源卡片。</summary>
    private const string CardXPath = "//article[contains(@class,'post-card')]";

    /// <summary>卡片标题链接（href 即详情页地址）。</summary>
    private const string TitleLinkXPath = ".//h2[contains(@class,'post-title')]//a";

    /// <summary>卡片分类：分类徽章中的第一个 span（第二个是 <span class="category-badge-count">N</span> 计数）。</summary>
    private const string CategoryTextXPath = ".//a[contains(@class,'category-badge')]//span[1]";

    /// <summary>卡片简介（部分卡片没有该元素，没有就不采集）。</summary>
    private const string DescXPath = ".//p[contains(@class,'post-desc')]";

    /// <summary>卡片封面图（post-media-single 内直出 img）。</summary>
    private const string MediaImgXPath = ".//div[contains(@class,'post-media-single')]//img";

    /// <summary>卡片内的网盘分享链接。</summary>
    private const string DiskLinkXPath = ".//a[contains(@class,'disk-link')]";

    /// <summary>详情页（/resource/N）标题。</summary>
    private const string DetailTitleXPath = "//h1[contains(@class,'resource-detail-title')]";

    /// <summary>分页导航容器。</summary>
    private const string PaginationXPath = "//nav[contains(@class,'pagination')]";

    /// <summary>下一页按钮。</summary>
    private const string NextPageXPath = ".//a[contains(@class,'pagination-step') and @rel='next']";

    /// <summary>页码数字链接。</summary>
    private const string PageNumXPath = ".//a[contains(@class,'pagination-num')]";

    /// <summary>分类入口链接（全站采集用）。</summary>
    private const string CategoryBadgeXPath = "//a[contains(@class,'category-badge')]";

    public Task<SiteCollectParseResult> ParseAsync(string html, Uri pageUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Task.FromResult(new SiteCollectParseResult
            {
                Status = SiteCollectParseStatus.StructureChanged,
                Message = "页面内容为空",
            });
        }

        HtmlDocument doc = Load(html);

        // 详情页（/resource/N）：有专属 h1 标题结构时单篇解析
        if (doc.DocumentNode.SelectSingleNode(DetailTitleXPath) != null)
        {
            return Task.FromResult(ParseDetail(doc, html, pageUri));
        }

        // 未知页面：交给通用解析器兜底（本站无卡片也无详情标题的新版结构）
        if (doc.DocumentNode.SelectSingleNode(CardXPath) == null)
        {
            return new GenericNetdiskSiteParser().ParseAsync(html, pageUri, cancellationToken);
        }

        return Task.FromResult(ParseList(doc, html, pageUri));
    }

    public SiteCollectNavResult ParseNav(string html, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SiteCollectNavResult();
        }
        return ExtractNav(Load(html), pageUri);
    }

    // ---------- DOM 装载 ----------

    private static HtmlDocument Load(string html)
    {
        HtmlDocument doc = new HtmlDocument
        {
            // 采集站常见未闭合标签，开启容错修复，避免整块内容被吞进上一个节点
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true,
        };
        doc.LoadHtml(html);
        RemoveNoiseNodes(doc);
        return doc;
    }

    /// <summary>移除 script/style/noscript，避免脚本中的 JSON（如 data-lightbox-images）混进链接与简介。</summary>
    private static void RemoveNoiseNodes(HtmlDocument doc)
    {
        HtmlNodeCollection? noise = doc.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (noise == null)
        {
            return;
        }
        foreach (HtmlNode node in noise.ToList())
        {
            node.Remove();
        }
    }

    // ---------- 列表页 ----------

    private static SiteCollectParseResult ParseList(HtmlDocument doc, string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult { IsListPage = true };
        result.Nav = ExtractNav(doc, pageUri);

        // 整页纯文本用于提取码的「链接附近窗口 + 整段扫描」兜底
        string plainText = SiteCollectHtmlUtil.ToPlainText(html);

        HtmlNodeCollection? cards = doc.DocumentNode.SelectNodes(CardXPath);
        if (cards == null || cards.Count == 0)
        {
            result.Status = SiteCollectParseStatus.StructureChanged;
            result.Message = "列表页未解析到任何资源卡片，站点模板可能已改版";
            return result;
        }

        foreach (HtmlNode card in cards)
        {
            if (result.Items.Count >= MaxListItems)
            {
                break;
            }

            HtmlNode? titleLink = card.SelectSingleNode(TitleLinkXPath);
            if (titleLink == null)
            {
                continue;
            }

            string name = ReadText(titleLink);
            if (name.Length == 0)
            {
                continue;
            }

            string detailUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, titleLink.GetAttributeValue("href", string.Empty));
            string intro = ReadText(card.SelectSingleNode(DescXPath));
            if (intro.Length > IntroMaxLength)
            {
                intro = SiteCollectHtmlUtil.Truncate(intro, IntroMaxLength);
            }
            string category = ReadText(card.SelectSingleNode(CategoryTextXPath));
            List<string> images = ExtractCardImages(card, pageUri);

            HtmlNodeCollection? diskLinks = card.SelectNodes(DiskLinkXPath);
            if (diskLinks == null || diskLinks.Count == 0)
            {
                continue; // 卡片没有可直接采集的网盘链接，跳过
            }

            foreach (HtmlNode disk in diskLinks)
            {
                string url = NetDiskDetector.NormalizeUrl(disk.GetAttributeValue("href", string.Empty));
                if (url.Length == 0)
                {
                    continue;
                }

                CollectedItem item = new CollectedItem
                {
                    Name = name,
                    Intro = intro,
                    Url = url,
                    Password = NetDiskDetector.ExtractPassword(plainText, url),
                    Category = category,
                    DetailUrl = detailUrl,
                    CloudType = NetDiskDetector.GetLinkType(url),
                };
                item.SetImages(images);
                result.Items.Add(item);
            }
        }

        if (result.Items.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "列表页卡片中未找到可采集的网盘链接";
            return result;
        }

        result.Status = SiteCollectParseStatus.Ok;
        return result;
    }

    // ---------- 详情页 ----------

    /// <summary>详情页（/resource/N）：标题 + 分类 + 封面图 + 全部盘链接，一条一链接拆开。</summary>
    private static SiteCollectParseResult ParseDetail(HtmlDocument doc, string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult { IsListPage = false };
        result.Nav = ExtractNav(doc, pageUri);

        string name = ReadText(doc.DocumentNode.SelectSingleNode(DetailTitleXPath));
        string category = ReadText(doc.DocumentNode.SelectSingleNode(CategoryTextXPath));
        List<string> images = ExtractCardImages(doc.DocumentNode, pageUri);
        string plainText = SiteCollectHtmlUtil.ToPlainText(html);

        HtmlNodeCollection? diskLinks = doc.DocumentNode.SelectNodes(DiskLinkXPath);
        if (diskLinks == null || diskLinks.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "详情页中未找到可采集的网盘链接";
            return result;
        }

        foreach (HtmlNode disk in diskLinks)
        {
            string url = NetDiskDetector.NormalizeUrl(disk.GetAttributeValue("href", string.Empty));
            if (url.Length == 0)
            {
                continue;
            }

            CollectedItem item = new CollectedItem
            {
                Name = name,
                Intro = string.Empty, // 详情页没有 post-desc 简介块
                Url = url,
                Password = NetDiskDetector.ExtractPassword(plainText, url),
                Category = category,
                DetailUrl = pageUri.AbsoluteUri,
                CloudType = NetDiskDetector.GetLinkType(url),
            };
            item.SetImages(images);
            result.Items.Add(item);
        }

        if (result.Items.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "详情页中未找到可采集的网盘链接";
            return result;
        }

        result.Status = SiteCollectParseStatus.Ok;
        return result;
    }

    /// <summary>卡片封面图：post-media-single 内的 img，去重后最多 5 张，首图作封面。</summary>
    private static List<string> ExtractCardImages(HtmlNode card, Uri pageUri)
    {
        HtmlNodeCollection? imgs = card.SelectNodes(MediaImgXPath);
        if (imgs == null)
        {
            return new List<string>();
        }

        return imgs
            .Select(img => SiteCollectHtmlUtil.NormalizeImageUrl(pageUri, img.GetAttributeValue("src", string.Empty)))
            .Where(url => SiteCollectHtmlUtil.IsUsableImageUrl(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SiteCollectHtmlUtil.MaxImageCount)
            .ToList();
    }

    // ---------- 分页导航 ----------

    /// <summary>提取「下一页」、总页数（页码数字最大值）与分类入口（/category/…）。</summary>
    private static SiteCollectNavResult ExtractNav(HtmlDocument doc, Uri pageUri)
    {
        SiteCollectNavResult nav = new SiteCollectNavResult();
        HtmlNode? pagination = doc.DocumentNode.SelectSingleNode(PaginationXPath);
        if (pagination != null)
        {
            string next = ReadAttribute(pagination.SelectSingleNode(NextPageXPath), "href");
            if (next.Length > 0)
            {
                string nextUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, next);
                if (IsSameHost(nextUrl, pageUri) && !IsSamePage(nextUrl, pageUri))
                {
                    nav.NextPageUrl = nextUrl;
                }
            }

            int maxPage = 0;
            HtmlNodeCollection? nums = pagination.SelectNodes(PageNumXPath);
            if (nums != null)
            {
                foreach (HtmlNode num in nums)
                {
                    if (int.TryParse(ReadText(num).Trim(), out int page) && page > maxPage)
                    {
                        maxPage = page;
                    }
                }
            }
            if (maxPage > 1)
            {
                nav.TotalPagesHint = maxPage;
            }
        }

        HashSet<string> categoryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HtmlNodeCollection? badges = doc.DocumentNode.SelectNodes(CategoryBadgeXPath);
        if (badges != null)
        {
            foreach (HtmlNode badge in badges)
            {
                if (nav.CategoryLinks.Count >= 30)
                {
                    break;
                }
                string href = badge.GetAttributeValue("href", string.Empty);
                string categoryUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, href);
                if (categoryUrl.Length == 0
                    || !IsSameHost(categoryUrl, pageUri)
                    || !categoryUrls.Add(categoryUrl))
                {
                    continue;
                }
                string categoryName = ReadText(badge.SelectSingleNode(".//span[1]"));
                if (categoryName.Length == 0)
                {
                    categoryName = ReadText(badge);
                }
                nav.CategoryLinks.Add(new SiteCollectNavLink
                {
                    Url = categoryUrl,
                    Name = SiteCollectHtmlUtil.Truncate(categoryName, 50),
                });
            }
        }

        return nav;
    }

    // ---------- 通用小工具 ----------

    /// <summary>取节点纯文本：HAP 的 InnerText 已解码实体，再交给 CleanText 压缩空白。</summary>
    private static string ReadText(HtmlNode? node)
    {
        return node == null ? string.Empty : SiteCollectHtmlUtil.CleanText(node.InnerText);
    }

    private static string ReadAttribute(HtmlNode? node, string name)
    {
        return node == null ? string.Empty : node.GetAttributeValue(name, string.Empty);
    }

    private static bool IsSameHost(string url, Uri pageUri)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri?.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePage(string url, Uri pageUri)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(
                uri?.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                pageUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
    }
}