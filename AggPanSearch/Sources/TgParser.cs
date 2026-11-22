using System.Text;
using System.Text.RegularExpressions;
using AggPanSearch.Detection;
using AggPanSearch.Models;

namespace AggPanSearch.Sources;

/// <summary>
/// Telegram 频道搜索结果页面解析器。
/// 对应 Go 项目 util/parser_util.go：从 t.me/s/频道 HTML 中提取网盘链接、密码、标题、标签、图片。
/// </summary>
public static class TgParser
{
    // Telegram 消息块容器
    private static readonly Regex MessageBlockPattern = RegexCache.Get(
        @"<div class=""tgme_widget_message[^>]*""(?<attrs>[^>]*)>(?<body>.*?)</div>\s*</div>\s*(?=<div class=""tgme_widget_message|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MessageWrapperPattern = RegexCache.Get(
        @"<div class=""tgme_widget_message_wrap[^>]*"">(?<content>.*?)(?=<div class=""tgme_widget_message_wrap|</div></div></div></div></div>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HrefPattern = RegexCache.Get(
        @"<a[^>]+href=""(?<url>[^""]+)""[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ImgPattern = RegexCache.Get(
        @"<img[^>]+src=""(?<src>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TagPattern = RegexCache.Get(
        @"<a[^>]+href=""#(?<tag>[^""]+)""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // 消息 ID：data-post="频道名/12345"
    private static readonly Regex PostIdPattern = RegexCache.GetIgnoreCase(
        @"data-post=""(?<channel>[^/""]+)/(?<id>\d+)""");

    // 时间：tgme_widget_message_date 的 datetime 属性
    private static readonly Regex DatePattern = RegexCache.GetIgnoreCase(
        @"class=""tgme_widget_message_date""[^>]*>\s*<time[^>]+datetime=""(?<dt>[^""]+)""");

    private static readonly Regex NoMessagesFoundPattern = RegexCache.Get(
        @"tme_no_messages_found|No messages found");

    private static readonly Regex StripTagsPattern = RegexCache.Get(@"<[^>]+>");

    /// <summary>
    /// 解析状态。
    /// </summary>
    public enum ParseStatus
    {
        Ok,
        NoMessages,
        StructureChanged,
    }

    /// <summary>
    /// 解析一段 TG 频道搜索 HTML 页面。
    /// </summary>
    /// <param name="html">完整 HTML。</param>
    /// <param name="channel">频道名（用于回填来源和 result.Channel）。</param>
    public static TgParseResult Parse(string? html, string channel)
    {
        var result = new TgParseResult();
        if (string.IsNullOrWhiteSpace(html))
        {
            result.Status = ParseStatus.StructureChanged;
            return result;
        }

        // 无结果标记
        if (NoMessagesFoundPattern.IsMatch(html))
        {
            result.Status = ParseStatus.NoMessages;
            return result;
        }

        // 提取所有消息包装块
        var blockMatches = MessageWrapperPattern.Matches(html);
        if (blockMatches.Count == 0)
        {
            result.Status = ParseStatus.StructureChanged;
            return result;
        }

        foreach (Match block in blockMatches)
        {
            var blockHtml = block.Groups["content"].Value;
            var item = ParseMessageBlock(blockHtml, channel);
            if (item is not null)
                result.Results.Add(item);
        }

        result.Status = result.Results.Count > 0 ? ParseStatus.Ok : ParseStatus.StructureChanged;
        return result;
    }

    private static SearchResult? ParseMessageBlock(string blockHtml, string channel)
    {
        // 消息 ID 与频道
        var postId = PostIdPattern.Match(blockHtml);
        var messageId = postId.Success ? $"{postId.Groups["channel"].Value}/{postId.Groups["id"].Value}" : "";
        var msgChannel = postId.Success ? postId.Groups["channel"].Value : channel;

        // 时间
        DateTime datetime = default;
        var dateMatch = DatePattern.Match(blockHtml);
        if (dateMatch.Success &&
            DateTime.TryParse(dateMatch.Groups["dt"].Value, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedDt))
        {
            datetime = parsedDt;
        }

        // 正文（去掉内联键盘等交互区域）
        var text = ExtractMessageText(blockHtml);

        // 提取网盘链接
        var links = ExtractLinksWithPassword(blockHtml, text);

        // 标签
        var tags = new List<string>();
        foreach (Match t in TagPattern.Matches(blockHtml))
        {
            var tag = HtmlDecode(t.Groups["tag"].Value);
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag))
                tags.Add(tag);
        }

        // 图片
        var images = new List<string>();
        foreach (Match img in ImgPattern.Matches(blockHtml))
        {
            var src = img.Groups["src"].Value;
            if (src.StartsWith("//"))
                src = "https:" + src;
            if (!string.IsNullOrWhiteSpace(src) && !images.Contains(src))
                images.Add(src);
        }

        if (links.Count == 0)
            return null; // 无网盘链接的消息不构成搜索结果

        // 标题提取
        var title = ExtractTitle(text);

        var result = new SearchResult
        {
            MessageId = messageId,
            Channel = msgChannel,
            Datetime = datetime,
            Title = title,
            Content = text,
            Links = links,
            Tags = tags,
            Images = images,
        };
        result.UniqueId = BuildUniqueId(result);
        result.ComputedUniqueId = result.UniqueId;
        return result;
    }

    /// <summary>提取正文纯文本（去除标签、解码实体，保留换行）。</summary>
    private static string ExtractMessageText(string blockHtml)
    {
        // 去掉 inline keyboard 这类只含按钮的区域，避免按钮文案混入正文
        var cleaned = RegexCache.GetIgnoreCase(@"<div class=""tgme_widget_message_inline_keyboard[\s\S]*?</div>\s*</div>")
            .Replace(blockHtml, "");

        // 去掉 <br> -> 换行，然后剥离所有标签
        cleaned = cleaned.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        var text = StripTagsPattern.Replace(cleaned, "");
        text = HtmlDecode(text);
        return CollapseWhitespace(text);
    }

    /// <summary>从消息块中提取网盘链接，并尝试关联密码。</summary>
    private static List<Link> ExtractLinksWithPassword(string blockHtml, string text)
    {
        var rawBook = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        // 来源1：按钮链接（inline keyboard 或消息里的 a[href]）
        if (blockHtml.Contains("tgme_widget_message", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match a in HrefPattern.Matches(blockHtml))
            {
                var url = a.Groups["url"].Value;
                AddCandidate(rawBook, url);
            }
        }

        // 来源2：正文裸链接
        foreach (var c in NetDiskDetector.ExtractNetDiskLinks(text))
            AddCandidate(rawBook, c.Url, c.Type);

        if (rawBook.Count == 0)
            return new List<Link>();

        var links = new List<Link>();
        foreach (var (url, cand) in rawBook)
        {
            var type = string.IsNullOrEmpty(cand.Type) || cand.Type == CloudTypes.Others
                ? NetDiskDetector.GetLinkType(url)
                : cand.Type;

            var password = NetDiskDetector.ExtractPassword(text, url);

            // 尝试从按钮 title 提取密码（某些 TG 里密码在按钮文本后）
            if (string.IsNullOrEmpty(password))
                password = TryGetPasswordFromHref(blockHtml, url);

            var link = new Link
            {
                Type = type,
                Url = url,
                Password = string.IsNullOrEmpty(password) ? null : password,
            };
            links.Add(link);
        }
        return links;
    }

    private static void AddCandidate(Dictionary<string, Candidate> book, string url, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        var norm = NetDiskDetector.NormalizeUrl(url);
        if (string.IsNullOrEmpty(norm))
            return;

        // 过滤 TG 页面内部噪音链接：渠道自身/消息链接、?q=#标签 搜索链接等
        if (IsTgInternalNoise(norm))
            return;

        // 仅保留能识别为具体网盘/磁力/电驴类型的链接（t.me 内部链接、?q= 标签链接等无法识别为资源）
        var linkType = type ?? NetDiskDetector.GetLinkType(norm);
        if (linkType == CloudTypes.Others)
            return;

        if (!book.ContainsKey(norm))
            book[norm] = new Candidate { Type = linkType, Url = norm };
    }

    /// <summary>判断是否为 TG 频道内部的噪音链接（自身/消息/标签搜索）。</summary>
    private static bool IsTgInternalNoise(string norm)
    {
        if (norm.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("http://t.me/", StringComparison.OrdinalIgnoreCase))
            return true;
        // 如 https://?q=%23标签 这样 query 形式的畸形链接
        if (norm.Contains("?q=", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("#", StringComparison.Ordinal))
            return true;
        // host 为 t.me / *.t.me 的链接
        if (Uri.TryCreate(norm, UriKind.Absolute, out var u) &&
            (u.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
             u.Host.EndsWith(".t.me", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static string TryGetPasswordFromHref(string blockHtml, string url)
    {
        // 找到包含 url 的 <a> 片段附近的文字，提取 4-6 位码
        var idx = blockHtml.IndexOf(url, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return string.Empty;
        var start = Math.Max(0, idx - 120);
        var end = Math.Min(blockHtml.Length, idx + url.Length + 120);
        return NetDiskDetector.ExtractPassword(StripTags(HtmlDecode(blockHtml[start..end])), url);
    }

    private static string StripTags(string s)
        => StripTagsPattern.Replace(s, "");

    /// <summary>提取标题：跳过日期头、元数据行、纯标签行、频道简介行，优先取"名称："后的内容。</summary>
    private static string ExtractTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            // 跳过日期头：📅 9月9日 等
            if (line.Contains("📅") || line.Contains('日') && line.Length <= 8)
                continue;

            // 跳过元数据行：类型：/简介：/时长：等
            if (line.StartsWith("类型") || line.StartsWith("简介") || line.StartsWith("时长")
                || line.StartsWith("大小") || line.StartsWith("年份") || line.StartsWith("导演"))
                continue;

            // 跳过频道固定简介/宣传语行（每条消息首行的自我介绍，非资源标题）
            if (IsChannelIntroLine(line))
                continue;

            // 跳过纯标签行
            var tagsOnly = RegexCache.Get(@"^[\s#\u4e00-\u9fa5A-Za-z0-9]+$");
            if (line.StartsWith('#') && tagsOnly.IsMatch(line))
                continue;

            // 优先取 "名称：" 后内容
            var nameIdx = line.IndexOf("名称：", StringComparison.Ordinal);
            if (nameIdx >= 0)
                return line[(nameIdx + 3)..].Trim();

            // 裁剪掉 "简介"/"描述"关键词后的内容
            var cutIdx = line.IndexOf("简介", StringComparison.Ordinal);
            if (cutIdx > 0)
                return line[..cutIdx].Trim();

            return line.Length > 120 ? line[..120] : line;
        }
        return string.Empty;
    }

    // 常见网盘类型词，用于识别"频道宣传语"这类由类型词+介绍词组成的行
    private static readonly HashSet<string> ChannelIntroPanTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "夸克", "百度", "阿里", "阿里云盘", "百度网盘", "夸克网盘", "迅雷", "迅雷云盘",
        "uc", "uc网盘", "115", "115网盘", "光鸭网盘", "光鸭", "天翼云盘", "天翼",
        "123", "123云盘", "123网盘", "移动云盘", "磁力", "种子", "网盘",
    };

    // 常见频道介绍词（与网盘类型一起构成频道宣传语）
    private static readonly HashSet<string> ChannelIntroWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "资源", "影视", "分享", "频道", "剧场", "短剧", "动漫", "音乐", "合集",
        "文件", "备份", "收藏", "发布", "日更", "每天更新",
    };

    /// <summary>判断某一行是否为频道固定简介/宣传语（如"夸克百度迅雷UC综合网盘频道"、
    /// "YOYO资源|夸克|UC|短剧|影视"、"懒狗集中营-115/阿里/百度/迅雷/夸克 影视分享..."）。
    /// 这类行会顶在每条消息首行，不应作为资源标题。
    /// 策略：真实资源标题很少同时罗列多种网盘类型，因此只要一行中出现了多个网盘类型词，基本可判定为频道简介。
    /// </summary>
    private static bool IsChannelIntroLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        // 含网盘链接/域名的行是真实资源内容，不是简介
        if (line.Contains("pan.quark.cn") || line.Contains("pan.baidu.com")
            || line.Contains("alipan.com") || line.Contains("pan.xunlei.com")
            || line.Contains("drive.uc.cn") || line.Contains("guangyapan.com")
            || line.Contains("pan.xunlei.com") || line.Contains("weiyun.com")
            || line.Contains("://") || line.Contains(".com/") || line.Contains(".cn/"))
            return false;

        // 统计行中出现的网盘类型词（去重后的种数）
        var panTypeCount = CountPanTypes(line);

        // 出现 2 个及以上不同网盘类型词 → 基本是频道宣传语（真实作品标题极少"夸克+百度+迅雷"并列）
        if (panTypeCount >= 2)
            return true;

        // "xx频道"（较短）→ 频道简介
        if (line.EndsWith("频道") && line.Length <= 20)
            return true;

        // 以 "|" 分隔的频道宣传语：影视动漫分享|SeedHub|夸克|网盘|🧲、YOYO资源|夸克|UC|短剧|影视
        if (line.Contains('|'))
        {
            var segs = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2)
            {
                var introCount = 0;
                foreach (var s in segs)
                {
                    var t = s.Trim();
                    if (IsChannelIntroToken(t))
                        introCount++;
                }
                // 过半段为介绍词 → 视为频道宣传语
                if (introCount * 2 >= segs.Length)
                    return true;
            }
        }

        // "xx频道"但略长（宣传语），如：懒狗集中营-115/阿里/百度/迅雷/夸克 影视分享 ...
        if (line.Contains("频道") && (line.Length <= 20 || line.Count(c => c == '/') >= 2))
            return true;

        return false;
    }

    /// <summary>统计一行中出现不同网盘类型词的种数（子串匹配）。</summary>
    private static int CountPanTypes(string line)
    {
        var hit = 0;
        foreach (var type in ChannelIntroPanTypes)
        {
            if (type.Length < 2)
                continue;
            if (line.Contains(type, StringComparison.OrdinalIgnoreCase))
                hit++;
        }
        return hit;
    }

    /// <summary>判断字符串是否为"网盘类型词"或"频道介绍词/宣传词"。</summary>
    private static bool IsChannelIntroToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        // 去掉首尾符号再判断
        token = token.Trim();
        if (ChannelIntroPanTypes.Contains(token) || ChannelIntroWords.Contains(token))
            return true;

        // 形如"xx资源""xx分享""xx影视"的组合
        foreach (var word in ChannelIntroWords)
        {
            if (token.EndsWith(word, StringComparison.Ordinal) && token.Length - word.Length <= 6)
                return true;
        }
        return false;
    }

    /// <summary>构建全局唯一 ID：channel + messageId + 标题哈希。</summary>
    private static string BuildUniqueId(SearchResult result)
    {
        if (!string.IsNullOrEmpty(result.MessageId))
            return $"{result.Channel}:{result.MessageId}";
        var hash = string.IsNullOrEmpty(result.Title)
            ? result.Content.GetHashCode(StringComparison.Ordinal)
            : result.Title.GetHashCode(StringComparison.Ordinal);
        return $"{result.Channel}:{result.Links.Count}:{hash:X}";
    }

    private static string HtmlDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingNl = 0;
        var pendingSp = false;
        foreach (var c in s)
        {
            if (c == '\n' || c == '\r')
            {
                pendingNl++;
                pendingSp = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSp = pendingNl == 0;
            }
            else
            {
                if (pendingSp)
                {
                    sb.Append(' ');
                    pendingSp = false;
                }
                if (pendingNl > 0)
                {
                    sb.Append('\n');
                    pendingNl = 0;
                }
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}

/// <summary>TG 解析结果。</summary>
public sealed class TgParseResult
{
    public TgParser.ParseStatus Status { get; set; }
    public List<SearchResult> Results { get; set; } = new();
}