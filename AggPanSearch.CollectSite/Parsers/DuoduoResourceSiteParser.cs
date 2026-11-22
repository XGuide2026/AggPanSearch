using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite.Parsers;

/// <summary>
/// 多多资源库（http://www.duoduoziyuanku.com/，Z-Blog PHP + TztCard 主题）采集解析器。
/// <para>列表页（首页 / page/N / category/N）：解析 tzt-media-box 卡片，得到标题、栏目分类、缩略图与详情页地址；</para>
/// <para>详情页（/post/N.html）：解析标题、栏目分类、简介、封面图与正文中的网盘链接。</para>
/// </summary>
public sealed class DuoduoResourceSiteParser : ISiteCollectParser
{
    private const string SiteHost = "duoduoziyuanku.com";
    private const int MaxListItems = 200;

    public string Name => "duoduoziyuanku";

    public int Priority => 10;

    public bool CanHandle(Uri pageUri)
    {
        return pageUri.Host.Contains(SiteHost, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 列表页 ----------

    private static readonly Regex StartTagPattern = RegexCache.Get(@"^<a\b[^>]*>", RegexOptions.IgnoreCase);

    private static readonly Regex HrefPattern = RegexCache.GetIgnoreCase(@"\bhref=[""'](?<h>[^""']+)[""']");

    private static readonly Regex TitleAttrPattern = RegexCache.GetIgnoreCase(@"\btitle=[""'](?<t>[^""']*)[""']");

    private static readonly Regex ListItemPattern = RegexCache.Get(
        @"<a\b[^>]*class=[""'][^""']*tzt-media-box[^""']*[""'][^>]*>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TimeBlockPattern = RegexCache.Get(
        @"<div\b[^>]*class=[""'][^""']*tzt-media-box_time[^""']*[""'][^>]*>(?<b>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex DescBlockPattern = RegexCache.Get(
        @"<div\b[^>]*class=[""'][^""']*tzt-media-box_desc[^""']*[""'][^>]*>(?<b>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SplitLineSpanPattern = RegexCache.Get(
        @"<span\b[^>]*class=[""'][^""']*split-line[^""']*[""'][^>]*>\s*</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ImgSrcPattern = RegexCache.GetIgnoreCase(@"<img\b[^>]+src=[""'](?<s>[^""']+)[""']");

    private static readonly Regex DatePattern = RegexCache.Get(@"^\d{4}-\d{2}-\d{2}");

    // ---------- 分页导航 ----------

    private static readonly Regex AnchorPattern = RegexCache.Get(
        @"<a\b(?<attrs>[^>]{0,2000})>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex RelNextPattern = RegexCache.GetIgnoreCase(@"\brel=[""']next[""']");

    private static readonly Regex TotalPagesPattern = RegexCache.Get(@"共\s*(?<n>\d{1,3})\s*页");

    private static readonly Regex CategoryPathPattern = RegexCache.Get(
        @"^/category/(?<id>\d+)/?$", RegexOptions.IgnoreCase);

    // ---------- 详情页 ----------

    private static readonly Regex ArticleTitlePattern = RegexCache.Get(
        @"<h1\b[^>]*class=[""'][^""']*tzt-article_title[^""']*[""'][^>]*>(?<t>.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SubtitBlockPattern = RegexCache.Get(
        @"<div\b[^>]*class=[""'][^""']*tzt-article_subtit[^""']*[""'][^>]*>(?<b>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex CategoryLinkPattern = RegexCache.Get(
        @"<a\b[^>]+href=[""'](?<h>[^""']*category/[^""']*)[""'][^>]*>(?<t>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // 简介只取正文容器（id=viewer / tzt-article_content）内的整段文本，不再使用 meta description 与整页文本

    /// <summary>
    /// 「下载链接 / 资源链接」等标签后面必须紧跟链接地址才视为简介结束标记。
    /// 不能用裸词「链接」截断，否则简介里的「如果资源链接失效了…」会被误判，只剩一个「如果」。
    /// 地址部分允许只出现「https:」，因为正文里的链接常被站点截成「https:...」。
    /// </summary>
    private static readonly Regex ResourceLinkCutPattern = RegexCache.Get(
        @"(?:下载|资源|获取|网盘|提取|保存)?链接\s*[:：]?\s*(?:https?:|pan\.|www\.|alipan|aliyundrive|cloud\.189|drive\.uc\.|115\.|mypikpak|guangyapan)",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 简介前缀标签：简介：/ 描述：/ 戏曲简介：/ 资源简介： 等，
    /// 以及「火影忍者的剧情简介 · · · · · ·」这种用间隔号收尾的标题。
    /// </summary>
    private static readonly Regex IntroLabelPattern = RegexCache.Get(
        @"^[^\s:：]{0,10}(?:简介|描述)(?:[:：]|\s+)(?:[·・．.\-—_*＊]\s*)*");

    /// <summary>正文容器之后常跟着「上一篇 / 下一篇 / 相关推荐 / 阅读：N 次」等站点模板文字。</summary>
    private static readonly Regex BoilerplateCutPattern = RegexCache.Get(
        @"(?:上一篇|下一篇|相关推荐|猜你喜欢|版权声明|免责声明|阅读\s*[:：]?\s*\d+\s*次)",
        RegexOptions.IgnoreCase);

    /// <summary>站点截断简介时留在结尾的省略号。</summary>
    private static readonly Regex TrailingEllipsisPattern = RegexCache.Get(@"(?:\s*(?:\.{2,}|…+|。{2,}))+\s*$");

    /// <summary>被截断后只剩半截协议头的地址尾巴（如「https:」「http://」）。</summary>
    private static readonly Regex TrailingLinkTailPattern = RegexCache.Get(@"(?:https?|ftp)\s*:?\s*/{0,2}\s*$", RegexOptions.IgnoreCase);

    /// <summary>简介入库长度上限。</summary>
    private const int IntroMaxLength = 500;

    // 正文容器起点：优先 tzt 模板的 id=viewer，其次 tzt-article_content
    private static readonly Regex ArticleBodyStartPattern = RegexCache.Get(
        @"id=[""'](?:viewer|tzt-article_content)[""']",
        RegexOptions.IgnoreCase);

    // 正文片段上限：避免详情页整页扫描回溯
    private const int DetailBodyMaxLength = 60000;

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

        if (IsDetailPage(html, pageUri))
        {
            return Task.FromResult(ParseDetail(html, pageUri));
        }

        if (html.Contains("tzt-media-box", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ParseList(html, pageUri));
        }

        // 既不是列表页也不是详情页：交给通用解析器兜底
        return new GenericNetdiskSiteParser().ParseAsync(html, pageUri, cancellationToken);
    }

    public SiteCollectNavResult ParseNav(string html, Uri pageUri)
    {
        return string.IsNullOrWhiteSpace(html) ? new SiteCollectNavResult() : ExtractNav(html, pageUri);
    }

    /// <summary>提取「下一页」、总页数（共16页）与分类入口（/category/{id}/）。</summary>
    private static SiteCollectNavResult ExtractNav(string html, Uri pageUri)
    {
        SiteCollectNavResult nav = new SiteCollectNavResult();

        Match total = TotalPagesPattern.Match(html);
        if (total.Success
            && int.TryParse(total.Groups["n"].Value, out int pages)
            && pages > 0 && pages <= 1000)
        {
            nav.TotalPagesHint = pages;
        }

        HashSet<string> categoryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorPattern.Matches(html))
        {
            string attrs = anchor.Groups["attrs"].Value;
            string href = HrefPattern.Match(attrs).Groups["h"].Value;
            if (href.Length == 0)
            {
                continue;
            }

            string text = SiteCollectHtmlUtil.CleanText(
                SiteCollectHtmlUtil.TagPatternForText(anchor.Groups["text"].Value));

            if (nav.NextPageUrl.Length == 0 && (text == "下一页" || text == "下页" || RelNextPattern.IsMatch(attrs)))
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

    private static bool IsDetailPage(string html, Uri pageUri)
    {
        if (pageUri.AbsolutePath.Contains("/post/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return html.Contains("tzt-article_content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析列表页：条目只有详情页地址，网盘链接需要二次抓取详情页。</summary>
    private static SiteCollectParseResult ParseList(string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult { IsListPage = true };
        result.Nav = ExtractNav(html, pageUri);

        foreach (Match match in ListItemPattern.Matches(html))
        {
            if (result.Items.Count >= MaxListItems)
            {
                break;
            }

            string startTag = StartTagPattern.Match(match.Value).Value;
            if (startTag.Length == 0)
            {
                continue;
            }

            string href = HrefPattern.Match(startTag).Groups["h"].Value;
            string detailUrl = SiteCollectHtmlUtil.ResolveUrl(pageUri, href);
            if (detailUrl.Length == 0)
            {
                continue;
            }

            string body = match.Groups["body"].Value;
            string title = SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.Decode(TitleAttrPattern.Match(startTag).Groups["t"].Value));
            if (title.Length == 0)
            {
                Match heading = ArticleTitlePattern.Match(body);
                title = heading.Success
                    ? SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(heading.Groups["t"].Value))
                    : string.Empty;
            }

            CollectedItem item = new CollectedItem
            {
                Name = title,
                Category = ExtractListCategory(body),
                ImageUrl = ExtractFirstImage(body, pageUri),
                Intro = ExtractListIntro(body),
                DetailUrl = detailUrl,
            };
            result.Items.Add(item);
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

    /// <summary>列表卡片中的栏目分类：时间行里除「置顶」和日期以外的文本。</summary>
    private static string ExtractListCategory(string body)
    {
        Match timeBlock = TimeBlockPattern.Match(body);
        if (!timeBlock.Success)
        {
            return string.Empty;
        }

        string withSeparator = SplitLineSpanPattern.Replace(timeBlock.Groups["b"].Value, "|");
        string text = SiteCollectHtmlUtil.CleanText(withSeparator);
        foreach (string raw in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0 || part.Length > 20)
            {
                continue;
            }
            if (part == "置顶" || part == "推荐" || part == "热门" || part == "最新" || part == "精华")
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

    private static string ExtractListIntro(string body)
    {
        Match desc = DescBlockPattern.Match(body);
        if (!desc.Success)
        {
            return string.Empty;
        }
        string text = SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(desc.Groups["b"].Value));
        // 首页卡片简介只显示占位省略号，过滤掉
        if (text.Replace(".", string.Empty).Replace("…", string.Empty).Length == 0)
        {
            return string.Empty;
        }
        return SiteCollectHtmlUtil.Truncate(text, 500);
    }

    /// <summary>解析详情页：标题 + 栏目分类 + 简介 + 封面图 + 正文网盘链接。</summary>
    private static SiteCollectParseResult ParseDetail(string html, Uri pageUri)
    {
        SiteCollectParseResult result = new SiteCollectParseResult();
        string plainText = SiteCollectHtmlUtil.ToPlainText(html);

        List<Candidate> links = NetDiskDetector.ExtractNetDiskLinks(plainText);
        if (links.Count == 0)
        {
            result.Status = SiteCollectParseStatus.NoItems;
            result.Message = "详情页未识别到网盘链接（链接可能是图片或需要登录后可见）";
            return result;
        }

        string name = ExtractDetailTitle(html, pageUri);
        string category = ExtractDetailCategory(html);
        string intro = ExtractDetailIntro(html);
        List<string> images = ExtractDetailImages(html, pageUri);

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

    private static string ExtractDetailTitle(string html, Uri pageUri)
    {
        Match title = ArticleTitlePattern.Match(html);
        if (title.Success)
        {
            string text = SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(title.Groups["t"].Value));
            if (text.Length > 0)
            {
                return text;
            }
        }
        return SiteCollectHtmlUtil.CleanText(pageUri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty);
    }

    /// <summary>详情页栏目：优先取 subtit 中指向 category 的链接文本。</summary>
    private static string ExtractDetailCategory(string html)
    {
        Match subtit = SubtitBlockPattern.Match(html);
        if (!subtit.Success)
        {
            return string.Empty;
        }

        Match categoryLink = CategoryLinkPattern.Match(subtit.Groups["b"].Value);
        if (categoryLink.Success)
        {
            string text = SiteCollectHtmlUtil.CleanText(
                SiteCollectHtmlUtil.TagPatternForText(categoryLink.Groups["t"].Value));
            if (text.Length > 0)
            {
                return text;
            }
        }

        // 兜底：整段去掉「栏目：」和时间后取第一段文本
        string raw = SplitLineSpanPattern.Replace(subtit.Groups["b"].Value, "|");
        string plain = SiteCollectHtmlUtil.CleanText(raw).Replace("栏目", string.Empty).Trim('：', ':', '|', ' ');
        foreach (string part in plain.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string value = part.Trim().Trim('：', ':');
            if (value.Length > 0 && value.Length <= 20 && !DatePattern.IsMatch(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 详情页简介：只取正文容器（id=viewer，其次 tzt-article_content）内的整段文本。
    /// 不使用 meta description（常被站点截断），也不回退整页文本，取不到时留空由列表页简介兜底。
    /// </summary>
    private static string ExtractDetailIntro(string html)
    {
        string bodyIntro = NormalizeIntro(SiteCollectHtmlUtil.ToPlainText(SliceArticleBody(html)));
        return SiteCollectHtmlUtil.Truncate(bodyIntro, IntroMaxLength);
    }

    /// <summary>清理简介：去标签前缀、截掉链接及其后的下载说明、去掉截断残留。</summary>
    private static string NormalizeIntro(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = SiteCollectHtmlUtil.CleanText(raw);
        text = IntroLabelPattern.Replace(text, string.Empty);

        // 简介后面紧跟「下载链接：https://...」，把链接及其后的内容截掉；
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

    /// <summary>定位正文片段：从 id=viewer / tzt-article_content 起，截到 &lt;/article&gt; 或片段上限。</summary>
    private static string SliceArticleBody(string html)
    {
        Match start = ArticleBodyStartPattern.Match(html);
        if (!start.Success)
        {
            return string.Empty;
        }

        // 起点回退到标签的「<」，否则片段开头会残留 id="viewer"> 这类半截标签文本，混进简介里
        int open = html.LastIndexOf('<', start.Index);
        string rest = html.Substring(open >= 0 ? open : start.Index);
        int end = rest.IndexOf("</article>", StringComparison.OrdinalIgnoreCase);
        if (end <= 0)
        {
            end = Math.Min(rest.Length, DetailBodyMaxLength);
        }
        return rest.Substring(0, end);
    }

    /// <summary>
    /// 详情页多图：正文容器优先，其次 og:image，最后全页兜底；
    /// 过滤主题 logo/图标、按页面顺序去重，最多 5 张。
    /// </summary>
    private static List<string> ExtractDetailImages(string html, Uri pageUri)
    {
        List<string> images = new List<string>();
        string body = SliceArticleBody(html);
        if (body.Length > 0)
        {
            images.AddRange(SiteCollectHtmlUtil.ExtractImages(body, pageUri));
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

    /// <summary>取一张封面图（列表页卡片用）：优先可用多图首图，其次首个可用 img。</summary>
    private static string ExtractFirstImage(string html, Uri pageUri)
    {
        List<string> images = SiteCollectHtmlUtil.ExtractImages(html, pageUri);
        if (images.Count > 0)
        {
            return images[0];
        }
        Match img = ImgSrcPattern.Match(html);
        if (!img.Success)
        {
            return string.Empty;
        }
        string url = SiteCollectHtmlUtil.NormalizeImageUrl(pageUri, img.Groups["s"].Value);
        return SiteCollectHtmlUtil.IsUsableImageUrl(url) ? url : string.Empty;
    }
}
