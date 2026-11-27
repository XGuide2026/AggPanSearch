using System.Text;
using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;
using HtmlAgilityPack;

namespace AggPanSearch.CollectSite.Parsers;

/// <summary>
/// 在线zzd站（https://www.onlinezzd.com/，Z-Blog PHP + TztCard 主题）的 HtmlAgilityPack 解析器，
/// 用 DOM 结构取卡片、正文容器与图片（网页内容解析不使用正则）。
/// <para>与多多资源库 HAP 版同模板，仅分类 URL 格式不同：本站为 /category-{id}.html（如 /category-4.html），
/// 多多资源库为 /category/{id}/。</para>
/// </summary>
public sealed class OnlinezzdHtmlAgilityPackSiteParser : ISiteCollectParser
{
    private const string SiteHost = "onlinezzd.com";
    private const int MaxListItems = 200;
    private const int IntroMaxLength = 500;

    /// <summary>超大页面不建 DOM（避免内存与耗时失控），超过时交给通用解析器兜底。</summary>
    private const int MaxHtmlLength = 4 * 1024 * 1024;

    public string Name => "onlinezzd-hap";

    public int Priority => 5;

    public bool CanHandle(Uri pageUri)
    {
        return pageUri.Host.Contains(SiteHost, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- XPath 选择器 ----------
    // 注意：XPath 的 class 值匹配大小写敏感，TztCard 主题固定小写；若遇大写 class 需用 translate() 包一层

    private const string ListCardXPath = "//a[contains(@class,'tzt-media-box')]";
    private const string ListTimeXPath = ".//div[contains(@class,'tzt-media-box_time')]";
    private const string ListDescXPath = ".//div[contains(@class,'tzt-media-box_desc')]";
    private const string ArticleTitleXPath = "//h1[contains(@class,'tzt-article_title')]";
    private const string ArticleSubtitXPath = "//div[contains(@class,'tzt-article_subtit')]";
    private const string ArticleBodyXPath = "//*[@id='viewer']";
    private const string ArticleBodyFallbackXPath = "//*[@id='tzt-article_content']";

    // 兼匹配 category/ 与 category- 前缀，覆盖同一主题不同分类 URL 风格
    private const string CategoryLinkXPath = ".//a[contains(@href,'category/') or contains(@href,'category-')]";

    // ---------- 简介清洗规则（与正则版完全一致） ----------

    private static readonly Regex ResourceLinkCutPattern = RegexCache.Get(
        @"(?:下载|资源|获取|网盘|提取|保存)?链接\s*[:：]?\s*(?:https?:|pan\.|www\.|alipan|aliyundrive|cloud\.189|drive\.uc\.|115\.|mypikpak|guangyapan)",
        RegexOptions.IgnoreCase);

    private static readonly Regex IntroLabelPattern = RegexCache.Get(
        @"^[^\s:：]{0,10}(?:简介|描述)(?:[:：]|\s+)(?:[·・．.\-—_*＊]\s*)*");

    private static readonly Regex BoilerplateCutPattern = RegexCache.Get(
        @"(?:上一篇|下一篇|相关推荐|猜你喜欢|版权声明|免责声明|阅读\s*[:：]?\s*\d+\s*次)",
        RegexOptions.IgnoreCase);

    private static readonly Regex TrailingEllipsisPattern = RegexCache.Get(@"(?:\s*(?:\.{2,}|…+|。{2,}))+\s*$");

    private static readonly Regex TrailingLinkTailPattern = RegexCache.Get(
        @"(?:https?|ftp)\s*:?\s*/{0,2}\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex TotalPagesPattern = RegexCache.Get(@"共\s*(?<n>\d{1,3})\s*页");

    // 本站分类 URL 格式：/category-{id}.html（如 /category-4.html），列 2 页为 /category-4_2.html
    private static readonly Regex CategoryPathPattern = RegexCache.Get(
        @"^/category-(?<id>\d+)\.html$", RegexOptions.IgnoreCase);

    private static readonly Regex DatePattern = RegexCache.Get(@"^\d{4}-\d{2}-\d{2}");

    // ---------- 入口 ----------

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

        if (html.Length > MaxHtmlLength)
        {
            return new GenericNetdiskSiteParser().ParseAsync(html, pageUri, cancellationToken);
        }

        HtmlDocument doc = Load(html);

        if (IsDetailPage(doc, pageUri))
        {
            return Task.FromResult(ParseDetail(doc, html, pageUri));
        }

        if (doc.DocumentNode.SelectSingleNode(ListCardXPath) != null)
        {
            return Task.FromResult(ParseList(doc, html, pageUri));
        }

        // 既不是列表页也不是详情页：交给通用解析器兜底
        return new GenericNetdiskSiteParser().ParseAsync(html, pageUri, cancellationToken);
    }

    public SiteCollectNavResult ParseNav(string html, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SiteCollectNavResult();
        }
        return ExtractNav(Load(html), html, pageUri);
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

    /// <summary>移除 script/style/noscript，避免脚本文字混进正文与链接识别。</summary>
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

    private static bool IsDetailPage(HtmlDocument doc, Uri pageUri)
    {
        if (pageUri.AbsolutePath.Contains("/post/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return doc.DocumentNode.SelectSingleNode(ArticleBodyXPath) != null
            || doc.DocumentNode.SelectSingleNode(ArticleBodyFallbackXPath) != null;
    }

    // ---------- 列表页 ----------

    private static SiteCollectParseResult ParseList(HtmlDocument doc, string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult { IsListPage = true };
        result.Nav = ExtractNav(doc, html, pageUri);

        HtmlNodeCollection? cards = doc.DocumentNode.SelectNodes(ListCardXPath);
        if (cards == null)
        {
            result.Status = SiteCollectParseStatus.StructureChanged;
            result.Message = "列表页未解析到任何资源条目，站点模板可能已改版";
            return result;
        }

        foreach (HtmlNode card in cards)
        {
            if (result.Items.Count >= MaxListItems)
            {
                break;
            }

            string detailUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, card.GetAttributeValue("href", string.Empty));
            if (detailUrl.Length == 0)
            {
                continue;
            }

            string title = SiteCollectHtmlUtil.CleanText(card.GetAttributeValue("title", string.Empty));
            if (title.Length == 0)
            {
                title = ReadText(card.SelectSingleNode(".//h1|.//h2|.//h3"));
            }

            result.Items.Add(new CollectedItem
            {
                Name = title,
                Category = ExtractListCategory(card),
                ImageUrl = ExtractFirstImage(card, pageUri),
                Intro = ExtractListIntro(card),
                DetailUrl = detailUrl,
            });
        }

        if (result.Items.Count == 0)
        {
            result.Status = SiteCollectParseStatus.StructureChanged;
            result.Message = "列表页未解析到任何资源条目，站点模板可能已改版";
            return result;
        }

        result.Status = SiteCollectParseStatus.Ok;
        return result;
    }

    /// <summary>列表卡片栏目：时间行里除「置顶」与日期以外的文本。</summary>
    private static string ExtractListCategory(HtmlNode card)
    {
        HtmlNode? timeBlock = card.SelectSingleNode(ListTimeXPath);
        if (timeBlock == null)
        {
            return string.Empty;
        }

        foreach (string raw in SplitBySeparator(timeBlock))
        {
            string part = SiteCollectHtmlUtil.CleanText(raw);
            if (part.Length == 0 || part.Length > 20)
            {
                continue;
            }
            if (part is "置顶" or "推荐" or "热门" or "最新" or "精华")
            {
                continue;
            }
            if (DatePattern.IsMatch(part))
            {
                continue;
            }
            return part;
        }
        return string.Empty;
    }

    private static string ExtractListIntro(HtmlNode card)
    {
        HtmlNode? desc = card.SelectSingleNode(ListDescXPath);
        if (desc == null)
        {
            return string.Empty;
        }
        string text = ReadText(desc);
        // 首页卡片简介只显示占位省略号，过滤掉
        if (text.Replace(".", string.Empty).Replace("…", string.Empty).Length == 0)
        {
            return string.Empty;
        }
        return SiteCollectHtmlUtil.Truncate(text, 500);
    }

    private static string ExtractFirstImage(HtmlNode card, Uri pageUri)
    {
        List<string> images = SiteCollectHtmlUtil.ExtractImages(card.InnerHtml, pageUri);
        return images.Count > 0 ? images[0] : string.Empty;
    }

    // ---------- 详情页 ----------

    private static SiteCollectParseResult ParseDetail(HtmlDocument doc, string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult();

        // 链接识别沿用文本工具，与正则版抽取口径保持一致（HAP 的 InnerText 会把相邻块文字粘连）
        string plainText = SiteCollectHtmlUtil.ToPlainText(html);
        List<Candidate> links = NetDiskDetector.ExtractNetDiskLinks(plainText);
        if (links.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "详情页未识别到网盘链接（链接可能是图片或需要登录后可见）";
            return result;
        }

        string name = ExtractDetailTitle(doc, pageUri);
        string category = ExtractDetailCategory(doc);
        string intro = ExtractDetailIntro(doc);
        List<string> images = ExtractDetailImages(doc, html, pageUri);

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
        return result;
    }

    private static string ExtractDetailTitle(HtmlDocument doc, Uri pageUri)
    {
        string text = ReadText(doc.DocumentNode.SelectSingleNode(ArticleTitleXPath));
        if (text.Length > 0)
        {
            return text;
        }
        return SiteCollectHtmlUtil.CleanText(pageUri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty);
    }

    /// <summary>详情页栏目：优先取 subtit 中指向 category（含 category/ 与 category- 两种前缀）的链接文本，其次取第一段非日期文本。</summary>
    private static string ExtractDetailCategory(HtmlDocument doc)
    {
        HtmlNode? subtit = doc.DocumentNode.SelectSingleNode(ArticleSubtitXPath);
        if (subtit == null)
        {
            return string.Empty;
        }

        string linkText = ReadText(subtit.SelectSingleNode(CategoryLinkXPath));
        if (linkText.Length > 0)
        {
            return linkText;
        }

        foreach (string raw in SplitBySeparator(subtit))
        {
            string value = SiteCollectHtmlUtil.CleanText(raw)
                .Replace("栏目", string.Empty)
                .Trim('：', ':', '|', ' ');
            if (value.Length > 0 && value.Length <= 20 && !DatePattern.IsMatch(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 详情页简介：只取正文容器（id=viewer，其次 tzt-article_content）内的整段文本。
    /// 相比正则版「截 HTML 片段再转文本」，这里直接取节点文本，不会切在半截标签上。
    /// </summary>
    private static string ExtractDetailIntro(HtmlDocument doc)
    {
        HtmlNode? body = doc.DocumentNode.SelectSingleNode(ArticleBodyXPath)
            ?? doc.DocumentNode.SelectSingleNode(ArticleBodyFallbackXPath);
        if (body == null)
        {
            return string.Empty;
        }
        return SiteCollectHtmlUtil.Truncate(NormalizeIntro(ReadText(body)), IntroMaxLength);
    }

    /// <summary>清理简介：去标签前缀、截掉链接及其后的下载说明、去掉截断残留（规则同正则版）。</summary>
    private static string NormalizeIntro(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = SiteCollectHtmlUtil.CleanText(raw);
        text = IntroLabelPattern.Replace(text, string.Empty);

        // 仅当「链接」后确实跟着链接地址时才截断，避免把「如果资源链接失效了…」误伤成只剩「如果」
        Match cut = ResourceLinkCutPattern.Match(text);
        if (cut.Success && cut.Index > 0)
        {
            text = text.Substring(0, cut.Index);
        }

        // 正文容器之后是「上一篇 / 下一篇 / 阅读：N 次」等模板文字
        Match boilerplate = BoilerplateCutPattern.Match(text);
        if (boilerplate.Success && boilerplate.Index > 0)
        {
            text = text.Substring(0, boilerplate.Index);
        }

        // 站点把简介截断时会留下「…」「...」或半截协议头
        text = TrailingEllipsisPattern.Replace(text, string.Empty);
        text = TrailingLinkTailPattern.Replace(text, string.Empty);
        text = TrailingEllipsisPattern.Replace(text, string.Empty);

        return text.Trim().TrimEnd('：', ':', ',', '，', ';', '；', '、', '|', '-').Trim();
    }

    /// <summary>详情页多图：正文容器 → og:image → 全页兜底，过滤噪声图并去重，最多 5 张。</summary>
    private static List<string> ExtractDetailImages(HtmlDocument doc, string html, Uri pageUri)
    {
        List<string> images = new List<string>();

        HtmlNode? body = doc.DocumentNode.SelectSingleNode(ArticleBodyXPath)
            ?? doc.DocumentNode.SelectSingleNode(ArticleBodyFallbackXPath);
        if (body != null)
        {
            images.AddRange(SiteCollectHtmlUtil.ExtractImages(body.InnerHtml, pageUri));
        }

        string ogImage = SiteCollectHtmlUtil.ExtractOgImage(html, pageUri);
        if (ogImage.Length > 0)
        {
            images.Add(ogImage);
        }

        if (images.Count < SiteCollectHtmlUtil.MaxImageCount)
        {
            images.AddRange(SiteCollectHtmlUtil.ExtractImages(html, pageUri));
        }

        return images
            .Where(SiteCollectHtmlUtil.IsUsableImageUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SiteCollectHtmlUtil.MaxImageCount)
            .ToList();
    }

    // ---------- 分页导航 ----------

    /// <summary>提取「下一页」、总页数（共19页）与分类入口（/category-{id}.html）。</summary>
    private static SiteCollectNavResult ExtractNav(HtmlDocument doc, string html, Uri pageUri)
    {
        SiteCollectNavResult nav = new SiteCollectNavResult();

        Match total = TotalPagesPattern.Match(SiteCollectHtmlUtil.ToPlainText(html));
        if (total.Success
            && int.TryParse(total.Groups["n"].Value, out int pages)
            && pages > 0 && pages <= 1000)
        {
            nav.TotalPagesHint = pages;
        }

        HtmlNodeCollection? anchors = doc.DocumentNode.SelectNodes("//a");
        if (anchors == null)
        {
            return nav;
        }

        HashSet<string> categoryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HtmlNode anchor in anchors)
        {
            string href = anchor.GetAttributeValue("href", string.Empty);
            if (href.Length == 0)
            {
                continue;
            }

            string text = ReadText(anchor);
            string rel = anchor.GetAttributeValue("rel", string.Empty);

            if (nav.NextPageUrl.Length == 0
                && (text == "下一页" || text == "下页" || string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase)))
            {
                string nextUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, href);
                if (nextUrl.Length > 0 && IsSameHost(nextUrl, pageUri) && !IsSamePage(nextUrl, pageUri))
                {
                    nav.NextPageUrl = nextUrl;
                }
            }

            if (nav.CategoryLinks.Count < 30 && text.Length > 0)
            {
                string categoryUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, href);
                if (categoryUrl.Length > 0
                    && IsSameHost(categoryUrl, pageUri)
                    && Uri.TryCreate(categoryUrl, UriKind.Absolute, out Uri categoryUri)
                    && CategoryPathPattern.IsMatch(categoryUri.AbsolutePath)
                    && categoryUrls.Add(categoryUrl))
                {
                    nav.CategoryLinks.Add(new SiteCollectNavLink
                    {
                        Url = categoryUrl,
                        Name = SiteCollectHtmlUtil.Truncate(text, 50),
                    });
                }
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

    /// <summary>按 split-line 分隔 span 把一行内容切成多段（与正则版替换分隔符的做法等价）。</summary>
    private static List<string> SplitBySeparator(HtmlNode container)
    {
        List<string> parts = new List<string>();
        StringBuilder buffer = new StringBuilder();
        CollectParts(container, parts, buffer);
        parts.Add(buffer.ToString());
        return parts;
    }

    private static void CollectParts(HtmlNode node, List<string> parts, StringBuilder buffer)
    {
        foreach (HtmlNode child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Element && HasClass(child, "split-line"))
            {
                parts.Add(buffer.ToString());
                buffer.Clear();
                continue;
            }

            if (child.NodeType == HtmlNodeType.Element && child.HasChildNodes)
            {
                CollectParts(child, parts, buffer);
                continue;
            }

            if (child.NodeType is HtmlNodeType.Text or HtmlNodeType.Element)
            {
                buffer.Append(child.InnerText);
            }
        }
    }

    private static bool HasClass(HtmlNode node, string className)
    {
        string value = node.GetAttributeValue("class", string.Empty);
        if (value.Length == 0)
        {
            return false;
        }
        foreach (string token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, className, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSameHost(string url, Uri pageUri)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && uri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePage(string url, Uri pageUri)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && string.Equals(
                uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                pageUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
    }
}