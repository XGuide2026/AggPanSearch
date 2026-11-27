using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;
using HtmlAgilityPack;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 网页采集用的轻量 HTML 文本工具。
/// 所有「解析网页内容（HTML 结构）」的操作一律用 HtmlAgilityPack DOM 实现，不用正则；
/// 保留的正则只处理纯文本/URL（实体解码后的空白压缩、面包屑前缀清理等）。
/// </summary>
public static class SiteCollectHtmlUtil
{
    private static readonly Regex BlankPattern = RegexCache.Get(@"[\s\u00a0]+");

    /// <summary>
    /// HTML 转纯文本：去噪声节点、解码实体、压缩空白。
    /// <para>实现说明：HAP 的 <see cref="HtmlNode.InnerText"/> 会把相邻块级元素文字直接粘连，
    /// 因此递归收集文本时在每个 Element 边界补空格，等价于原「标签替换为空格」的口径，
    /// 保证网盘链接/提取码与文字之间保留分隔（链接识别依赖此输出）。</para>
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }
        return GetPlainText(LoadDocument(html));
    }

    /// <summary>从已装载的 DOM 提取纯文本（供解析器在单次 Load 内复用，避免重复构建 DOM）。</summary>
    internal static string GetPlainText(HtmlDocument doc)
    {
        StringBuilder sb = new StringBuilder(doc.Text.Length / 4 + 256);
        AppendTextValue(doc.DocumentNode, sb);
        return CleanText(sb.ToString());
    }

    /// <summary>递归收集节点文本：文本节点直接追加，Element 节点边界补空格，注释/脚本等噪声节点跳过。</summary>
    private static void AppendTextValue(HtmlNode node, StringBuilder sb)
    {
        foreach (HtmlNode child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                sb.Append(child.InnerText);
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element)
            {
                continue;
            }
            sb.Append(' ');
            AppendTextValue(child, sb);
            sb.Append(' ');
        }
    }

    /// <summary>装载 HTML DOM（开启容错修复），并移除 script/style/noscript 与正文无关的节点。</summary>
    internal static HtmlDocument LoadDocument(string html)
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
    internal static void RemoveNoiseNodes(HtmlDocument doc)
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

    // src 与常见懒加载属性；懒加载地址优先于占位的 src
    private static readonly string[] LazyImageAttributes = { "data-original", "data-src", "data-lazy-src", "data-echo" };

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
    /// 只扫描前 40 个 img 节点，保证复杂度线性。
    /// </summary>
    public static List<string> ExtractImages(string? html, Uri baseUri)
    {
        if (string.IsNullOrEmpty(html))
        {
            return new List<string>();
        }
        return ExtractImages(LoadDocument(html), baseUri);
    }

    /// <summary>从已装载的 DOM 提取图片（供解析器在单次 Load 内复用，避免重复构建 DOM）。</summary>
    internal static List<string> ExtractImages(HtmlDocument doc, Uri baseUri)
    {
        List<string> images = new List<string>();
        HtmlNodeCollection? imgs = doc.DocumentNode.SelectNodes("//img");
        if (imgs == null)
        {
            return images;
        }
        int scanned = 0;
        foreach (HtmlNode img in imgs)
        {
            if (scanned++ >= 40 || images.Count >= MaxImageCount)
            {
                break;
            }
            if (IsSmallImageTag(img))
            {
                continue;
            }
            string url = ResolveImageSource(img, baseUri);
            if (url.Length == 0 || images.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            images.Add(url);
        }
        return images;
    }

    /// <summary>
    /// 从 img 节点中取图片地址：懒加载属性（data-original 等）优先，src 作为兜底。
    /// </summary>
    private static string ResolveImageSource(HtmlNode img, Uri baseUri)
    {
        foreach (string attrName in LazyImageAttributes)
        {
            string value = img.GetAttributeValue(attrName, string.Empty);
            if (value.Length == 0)
            {
                continue;
            }
            string url = NormalizeImageUrl(baseUri, value);
            if (url.Length > 0 && IsUsableImageUrl(url))
            {
                return url;
            }
        }
        string fallback = NormalizeImageUrl(baseUri, img.GetAttributeValue("src", string.Empty));
        return IsUsableImageUrl(fallback) ? fallback : string.Empty;
    }

    /// <summary>取页面 og:image / twitter:image（过滤噪声图后返回首个可用地址）。</summary>
    public static string ExtractOgImage(string? html, Uri baseUri)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }
        return ExtractOgImage(LoadDocument(html), baseUri);
    }

    /// <summary>从已装载的 DOM 取 og:image / twitter:image（供解析器在单次 Load 内复用）。</summary>
    internal static string ExtractOgImage(HtmlDocument doc, Uri baseUri)
    {
        HtmlNodeCollection? metas = doc.DocumentNode.SelectNodes("//meta");
        if (metas == null)
        {
            return string.Empty;
        }
        int scanned = 0;
        foreach (HtmlNode meta in metas)
        {
            if (scanned++ >= 60)
            {
                break;
            }
            string lower = meta.OuterHtml.ToLowerInvariant();
            if (!lower.Contains("og:image") && !lower.Contains("twitter:image"))
            {
                continue;
            }
            string url = NormalizeImageUrl(baseUri, meta.GetAttributeValue("content", string.Empty));
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

    /// <summary>显式声明宽高的图片：&lt;= 60px 基本是图标/占位图，跳过。</summary>
    private static bool IsSmallImageTag(HtmlNode img)
    {
        if (TryParseSize(img, "width") || TryParseSize(img, "height"))
        {
            return true;
        }
        return false;
    }

    private static bool TryParseSize(HtmlNode img, string attrName)
    {
        string value = img.GetAttributeValue(attrName, string.Empty);
        return int.TryParse(value, out int size) && size > 0 && size <= 60;
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
        // 去掉常见前缀（纯文本处理，非解析 HTML 结构）
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