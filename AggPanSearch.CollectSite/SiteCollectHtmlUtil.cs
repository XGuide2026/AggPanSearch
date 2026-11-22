using System.Net;
using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 网页采集用的轻量 HTML 文本工具（不引入第三方 HTML 解析库，与 TgParser 的正则风格保持一致）。
/// </summary>
public static class SiteCollectHtmlUtil
{
    private static readonly Regex CommentPattern = RegexCache.Get(@"<!--.*?-->", RegexOptions.Singleline);

    private static readonly Regex ScriptStylePattern = RegexCache.Get(
        @"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // 有界标签正则：单标签内属性最多 2 万字符，避免超长 style 属性导致回溯
    private static readonly Regex TagPattern = RegexCache.Get(@"<[^>]{0,20000}>");

    private static readonly Regex BlankPattern = RegexCache.Get(@"[\s\u00a0]+");

    /// <summary>去掉注释、script、style 等与正文无关的大块内容。</summary>
    public static string RemoveNoise(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }
        string result = CommentPattern.Replace(html, " ");
        result = ScriptStylePattern.Replace(result, " ");
        return result;
    }

    /// <summary>小片段转文本：去噪声、去标签（保留空白），用于 h1/title/面包屑等短文本。</summary>
    public static string TagPatternForText(string? html)
    {
        return string.IsNullOrEmpty(html) ? string.Empty : TagPattern.Replace(RemoveNoise(html), " ");
    }

    /// <summary>HTML 转纯文本：去噪声、去标签、解码实体、压缩空白。</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }
        string text = TagPattern.Replace(RemoveNoise(html), " ");
        return CleanText(text);
    }

    /// <summary>解码 HTML 实体并压缩空白。</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string decoded = WebUtility.HtmlDecode(text);
        return BlankPattern.Replace(decoded, " ").Trim();
    }

    /// <summary>仅解码 HTML 实体，保留原始空白（用于 title 属性等）。</summary>
    public static string Decode(string? text)
    {
        return string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlDecode(text);
    }

    /// <summary>把相对地址补全为绝对地址；无法解析时返回空串。</summary>
    public static string ResolveUrl(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }
        string value = Decode(href).Trim();
        if (value.Length == 0 || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (Uri.TryCreate(baseUri, value, out Uri? absolute))
        {
            return absolute.AbsoluteUri;
        }
        return string.Empty;
    }

    /// <summary>按最大长度截断字符串（超长直接截断，不做省略号）。</summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text.Length <= maxLength ? text : text.Substring(0, maxLength);
    }

    /// <summary>校验并创建 http/https 绝对地址（规则统一在 <see cref="SiteCollectUrlRules"/>）。</summary>
    public static bool TryCreateHttpUri(string? url, out Uri uri)
    {
        return SiteCollectUrlRules.TryCreateHttpUri(url, out uri);
    }

    #region 图片提取

    /// <summary>单条资源最多保留的图片数量。</summary>
    public const int MaxImageCount = 5;

    // 有界标签正则：单个 img 标签内最多 2 万字符，避免超长属性导致回溯
    private static readonly Regex ImgTagPattern = RegexCache.Get(@"<img\b[^>]{0,20000}>", RegexOptions.IgnoreCase);

    private static readonly Regex MetaTagPattern = RegexCache.Get(@"<meta\b[^>]{0,2000}>", RegexOptions.IgnoreCase);

    // src 与常见懒加载属性；懒加载地址优先于占位的 src
    private static readonly Regex ImgSourceAttrPattern = RegexCache.Get(
        @"\b(?<n>data-original|data-src|data-lazy-src|data-echo|src)\s*=\s*(?:""(?<v>[^""]{0,2000})""|'(?<v>[^']{0,2000})'|(?<v>[^\s>]{1,2000}))",
        RegexOptions.IgnoreCase);

    private static readonly Regex ContentAttrPattern = RegexCache.Get(
        @"\bcontent\s*=\s*(?:""(?<v>[^""]{0,2000})""|'(?<v>[^']{0,2000})')",
        RegexOptions.IgnoreCase);

    // 显式声明宽高的图片：<= 60px 基本是图标/占位图
    private static readonly Regex ImageSizeAttrPattern = RegexCache.Get(
        @"\b(?:width|height)\s*=\s*[""']?(?<v>\d{1,5})", RegexOptions.IgnoreCase);

    private static readonly string[] ImageNoiseTokens =
    {
        "/themes/", "/theme/", "favicon", "logo.", "logo_", "logo-", "/logo",
        "avatar", "qrcode", "qr_code", "qr-", "spacer", "blank.", "placeholder",
        "loading.", "noimage", "no-image", "1x1.", "pixel.", "spacer.",
    };

    private static readonly string[] ImageAllowedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// 把 img 上的地址规范化为可入库的绝对地址：
    /// 解码实体、补全相对路径、只保留 http/https、扩展名白名单，并把地址内的分号转义。
    /// </summary>
    public static string NormalizeImageUrl(Uri baseUri, string? raw)
    {
        string url = ResolveUrl(baseUri, raw);
        if (url.Length == 0 || !TryCreateHttpUri(url, out Uri uri))
        {
            return string.Empty;
        }
        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension.Length > 0 && !ImageAllowedExtensions.Contains(extension))
        {
            return string.Empty;
        }
        // 字段用分号分隔多图，地址自带的分号必须转义，避免拆分错位
        return url.Replace(";", "%3B");
    }

    /// <summary>
    /// 判断图片地址是否为可用图片（保守黑名单：主题 logo、头像、二维码、占位图等），
    /// 命中黑名单返回 false，不确定时返回 true。
    /// </summary>
    public static bool IsUsableImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        string lower = url.ToLowerInvariant();
        foreach (string token in ImageNoiseTokens)
        {
            if (lower.Contains(token))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 按页面顺序提取图片地址：过滤小图标/主题 logo、去重，最多取 <see cref="MaxImageCount"/> 张。
    /// 只扫描前 40 个 img 标签，保证复杂度线性。
    /// </summary>
    public static List<string> ExtractImages(string? html, Uri baseUri)
    {
        List<string> images = new List<string>();
        if (string.IsNullOrEmpty(html))
        {
            return images;
        }
        string content = RemoveNoise(html);
        int scanned = 0;
        foreach (Match tag in ImgTagPattern.Matches(content))
        {
            if (scanned++ >= 40 || images.Count >= MaxImageCount)
            {
                break;
            }
            if (IsSmallImageTag(tag.Value))
            {
                continue;
            }
            string url = ResolveImageSource(tag.Value, baseUri);
            if (url.Length == 0 || images.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            images.Add(url);
        }
        return images;
    }

    /// <summary>
    /// 从 img 标签中取图片地址：懒加载属性（data-original 等）优先，src 作为兜底。
    /// </summary>
    private static string ResolveImageSource(string tag, Uri baseUri)
    {
        string fallback = string.Empty;
        foreach (Match match in ImgSourceAttrPattern.Matches(tag))
        {
            string url = NormalizeImageUrl(baseUri, match.Groups["v"].Value);
            if (url.Length == 0 || !IsUsableImageUrl(url))
            {
                continue;
            }
            if (!match.Groups["n"].Value.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
            if (fallback.Length == 0)
            {
                fallback = url;
            }
        }
        return fallback;
    }

    /// <summary>取页面 og:image / twitter:image（过滤噪声图后返回首个可用地址）。</summary>
    public static string ExtractOgImage(string? html, Uri baseUri)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }
        string content = RemoveNoise(html);
        int scanned = 0;
        foreach (Match tag in MetaTagPattern.Matches(content))
        {
            if (scanned++ >= 60)
            {
                break;
            }
            string lower = tag.Value.ToLowerInvariant();
            if (!lower.Contains("og:image") && !lower.Contains("twitter:image"))
            {
                continue;
            }
            Match contentAttr = ContentAttrPattern.Match(tag.Value);
            if (!contentAttr.Success)
            {
                continue;
            }
            string url = NormalizeImageUrl(baseUri, contentAttr.Groups["v"].Value);
            if (url.Length > 0 && IsUsableImageUrl(url))
            {
                return url;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 拼接入库用的多图串：去重、限量、按条累加；放不下的地址整条丢弃，绝不产生半截地址。
    /// </summary>
    public static string JoinImages(IEnumerable<string>? images, int maxLength = 1000, int maxCount = MaxImageCount)
    {
        if (images == null)
        {
            return string.Empty;
        }
        List<string> picked = new List<string>();
        int length = 0;
        foreach (string? image in images)
        {
            if (picked.Count >= maxCount)
            {
                break;
            }
            string url = (image ?? string.Empty).Trim();
            if (url.Length == 0 || url.Length > maxLength || !IsUsableImageUrl(url) ||
                picked.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            int nextLength = length == 0 ? url.Length : length + 1 + url.Length;
            if (nextLength > maxLength)
            {
                break;
            }
            picked.Add(url);
            length = nextLength;
        }
        return string.Join(";", picked);
    }

    /// <summary>按分号拆分多图串（去空、去重、保序）。</summary>
    public static List<string> SplitImages(string? images)
    {
        if (string.IsNullOrWhiteSpace(images))
        {
            return new List<string>();
        }
        return images.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSmallImageTag(string tag)
    {
        foreach (Match match in ImageSizeAttrPattern.Matches(tag))
        {
            if (int.TryParse(match.Groups["v"].Value, out int size) && size > 0 && size <= 60)
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    /// <summary>
    /// 清理面包屑/栏目文本：去掉「首页 / 当前位置 / 您的位置」等前缀与分隔符。
    /// </summary>
    public static string CleanCategoryText(string? text)
    {
        string value = CleanText(text);
        if (value.Length == 0)
        {
            return string.Empty;
        }
        // 去掉常见前缀
        value = RegexCache.Get(@"^(当前位置|您的位置|当前路径|所在位置|位置|首页|主页)\s*[:：]?\s*").Replace(value, string.Empty);
        // 分隔符归一化后取最后一段（最具体的分类）
        value = value.Replace("»", ">").Replace("›", ">").Replace("／", ">").Replace("/", ">");
        string[] parts = value.Split('>', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string part = CleanText(parts[i]);
            if (part.Length > 0 && part != "首页" && part != "主页" && part != "当前位置" && part != "您的位置")
            {
                return part;
            }
        }
        return string.Empty;
    }
}
