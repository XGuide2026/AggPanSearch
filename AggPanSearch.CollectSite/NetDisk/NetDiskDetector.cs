using System.Text.RegularExpressions;

namespace AggPanSearch.CollectSite.NetDisk;

/// <summary>
/// 网盘链接识别、提取与标准化。
/// 对应 Go 项目 regex_util.go 中的链接类型识别（GetLinkType）、链接清理（Clean 系列）与密码提取逻辑。
/// </summary>
public static class NetDiskDetector
{
    // ---------- 网盘分享链接正则 ----------

    // 百度网盘：pan.baidu.com/s/分享码，可带 ?pwd=
    internal static readonly Regex BaiduPanPattern = RegexCache.Get(
        @"(?:https?://)?pan\.baidu\.com/s/[a-zA-Z0-9_\-]+(?:\?pwd=[a-zA-Z0-9]+)?");

    // 夸克网盘：pan.quark.cn/s/分享码
    internal static readonly Regex QuarkPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?pan\.quark\.cn/s/[a-zA-Z0-9]+");

    // 迅雷网盘：pan.xunlei.com/s/分享码，可带 ?pwd= 或 # 密码
    internal static readonly Regex XunleiPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?pan\.xunlei\.com/s/[a-zA-Z0-9]+(?:\?pwd=[a-zA-Z0-9]+#?[a-zA-Z0-9]*|#[a-zA-Z0-9]+)?");

    // 天翼云盘：cloud.189.cn/t/分享码，可带 URL 编码的访问码
    internal static readonly Regex TianyiPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?cloud\.189\.cn/t/[a-zA-Z0-9]+(?:[%][0-9A-Fa-f]{2})*");

    // UC 网盘：drive.uc.cn/s/分享码，可带 ?public=
    internal static readonly Regex UCPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?drive\.uc\.cn/s/[a-zA-Z0-9]+(?:\?public=.+)?");

    // 123 网盘（多域名）
    internal static readonly Regex Pan123Pattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?123(?:684|685|865|912|pan|592)\.(?:com|cn)/s/[a-zA-Z0-9]+");

    // 115 网盘
    internal static readonly Regex Pan115Pattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?(?:115\.com|115cdn\.com|anxia\.com)/s/[a-zA-Z0-9]+(?:\?password=[a-zA-Z0-9]+)?");

    // 阿里云盘：alipan.com/aliyundrive.com/s/分享码
    internal static readonly Regex AliyunPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?(?:alipan|aliyundrive)\.com/s/[a-zA-Z0-9]+");

    // 光鸭云盘
    internal static readonly Regex GuangyaPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?guangyapan\.com/s/[a-zA-Z0-9_\-]+");

    // 移动云盘
    internal static readonly Regex MobilePanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?(?:yun\.139\.com/shareweb|caiyun\.139\.com)/(?:[^\s，。；;]+)");

    // PikPak：mypikpak.com/s/ 或 pikpak.xyz ...
    internal static readonly Regex PikpakPanPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?(?:mypikpak\.com|mypikpak\.xyz)/s/[a-zA-Z0-9]+");

    // 磁力链接
    internal static readonly Regex MagnetPattern = RegexCache.GetIgnoreCase(
        @"magnet:\?xt=urn:[a-zA-Z0-9]+:[a-zA-Z0-9]+[^\s，。；;]*");

    // 电驴链接
    internal static readonly Regex Ed2kPattern = RegexCache.GetIgnoreCase(
        @"ed2k://[^\s，。；;]+");

    // ---------- 密码相关的正则 ----------

    /// <summary>通用：提取码：xxxx / 提取码:xxxx（4 位）</summary>
    internal static readonly Regex PasswordPattern = RegexCache.Get(
        @"提取码[：:]\s*([a-zA-Z0-9]{4})");

    /// <summary>天翼访问码：访问码：xxxx</summary>
    internal static readonly Regex TianyiAccessCodePattern = RegexCache.Get(
        @"访问码[：:]\s*([a-zA-Z0-9]{4})");

    /// <summary>迅雷：?pwd=xxxx</summary>
    internal static readonly Regex XunleiPwdPattern = RegexCache.Get(
        @"[?#]pwd=([a-zA-Z0-9]+)");

    /// <summary>115：password=xxxx</summary>
    internal static readonly Regex Pan115PasswordPattern = RegexCache.GetIgnoreCase(
        @"password=([a-zA-Z0-9]{1,8})");

    /// <summary>URL 参数：?pwd=xxxx</summary>
    internal static readonly Regex UrlPasswordPattern = RegexCache.Get(
        @"[?&#]pwd=([a-zA-Z0-9]{1,8})");

    /// <summary>天翼 URL 编码访问码：%E6%8F%90%E5%8F%96%E7%A0%81 后的 4 位</summary>
    private static readonly Regex TianyiEncodedAccessCodePattern = RegexCache.Get(
        @"%E6%8F%90%E5%8F%96%E7%A0%81[%29]*([a-zA-Z0-9]{4})");

    // ---------- 主入口：从一段文本中提取所有网盘链接（可回填类型） ----------

    /// <summary>
    /// 从文本中提取所有网盘分享链接，并为每个链接识别网盘类型。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>链接与其类型的映射（未识别类型为 null 或 "others"）。</returns>
    public static List<Candidate> ExtractNetDiskLinks(string? text)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAll(IEnumerable<Match> matches, string type)
        {
            foreach (var m in matches)
            {
                var rawUrl = m.Value.Trim();
                // 去掉尾部多余符号
                rawUrl = StripTrailing(rawUrl);
                if (rawUrl.Length == 0)
                    continue;

                var normalized = NormalizeUrl(rawUrl);
                if (normalized.Length == 0)
                    continue;

                if (!seen.Add(normalized))
                    continue;

                result.Add(new Candidate { Type = type, Url = normalized });
            }
        }

        AddAll(BaiduPanPattern.Matches(text), CloudTypes.Baidu);
        AddAll(QuarkPanPattern.Matches(text), CloudTypes.Quark);
        AddAll(AliyunPanPattern.Matches(text), CloudTypes.Aliyun);
        AddAll(GuangyaPanPattern.Matches(text), CloudTypes.Guangya);
        AddAll(TianyiPanPattern.Matches(text), CloudTypes.Tianyi);
        AddAll(UCPanPattern.Matches(text), CloudTypes.Uc);
        AddAll(MobilePanPattern.Matches(text), CloudTypes.Mobile);
        AddAll(Pan115Pattern.Matches(text), CloudTypes.Pan115);
        AddAll(PikpakPanPattern.Matches(text), CloudTypes.Pikpak);
        AddAll(XunleiPanPattern.Matches(text), CloudTypes.Xunlei);
        AddAll(Pan123Pattern.Matches(text), CloudTypes.Pan123);
        AddAll(MagnetPattern.Matches(text), CloudTypes.Magnet);
        AddAll(Ed2kPattern.Matches(text), CloudTypes.Ed2k);

        return result;
    }

    /// <summary>
    /// 识别单个链接的类型。无法识别时返回 "others"。
    /// 对应 Go 项目的 GetLinkType 判断顺序。
    /// </summary>
    public static string GetLinkType(string url)
    {
        var u = url;
        // 移动云盘有两种格式，先做较长域名判断
        if (u.Contains("ed2k://", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Ed2k;
        if (u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Magnet;
        if (u.Contains("pan.baidu.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Baidu;
        if (u.Contains("pan.quark.cn", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Quark;
        if (u.Contains("alipan.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("aliyundrive.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Aliyun;
        if (u.Contains("guangyapan.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Guangya;
        if (u.Contains("cloud.189.cn", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Tianyi;
        if (u.Contains("drive.uc.cn", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Uc;
        if (u.Contains("yun.139.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("caiyun.139.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Mobile;
        if (u.Contains("115.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("115cdn.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("anxia.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Pan115;
        if (u.Contains("mypikpak", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Pikpak;
        if (u.Contains("pan.xunlei.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Xunlei;
        if (u.Contains("123684.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("123685.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("123865.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("123912.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("123pan.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("123592.com", StringComparison.OrdinalIgnoreCase))
            return CloudTypes.Pan123;
        return CloudTypes.Others;
    }

    // ---------- 链接标准化 ----------

    /// <summary>
    /// 规范化链接：补充协议头、URL 解码、剔除尾部垃圾。
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var u = url.Trim();
        if (u.Length == 0)
            return string.Empty;

        // 去除首尾的引号/括号等包裹符
        u = u.Trim('"', '\'', '(', ')', '[', ']');

        // 补协议头
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase))
        {
            u = "https://" + u;
        }

        // 截断到链接主体（从协议后找到第一个空白/中文标点）
        var traversed = TryTrimTrailing(u);
        return traversed;
    }

    private static string TryTrimTrailing(string url)
    {
        // 找到协议头后的主体起点
        var idx = url.IndexOf("://", StringComparison.Ordinal);
        var start = idx >= 0 ? idx + 3 : 0;
        var i = start;
        // 截断遇到第一个空白、中文标点、表情符号、换行
        for (; i < url.Length; i++)
        {
            var c = url[i];
            if (char.IsWhiteSpace(c) || c == '，' || c == '。' || c == '；' || c == '；' ||
                (c >= 0x1F300 && c <= 0x1FAFF)) // 表情范围
            {
                break;
            }
        }
        var body = url[start..i].Trim();

        // 去掉 body 尾部像 ?/& 这样的悬挂
        body = body.TrimEnd('?', '&', '#', '/');

        // 重新拼回协议头
        return url[..start] + body;
    }

    /// <summary>去掉链接尾部多余的标点/符号。</summary>
    private static string StripTrailing(string raw)
    {
        // 简单方式：交给 NormalizeUrl 处理
        return raw.Trim();
    }

    // ---------- 密码提取 ----------

    /// <summary>
    /// 从整段上下文中提取某个链接的密码（提取码/访问码）。
    /// 优先使用"链接附近短窗口"策略，失败则整段扫描。
    /// 对应 Go 项目 extractCodeNear + ExtractPassword 逻辑。
    /// </summary>
    public static string ExtractPassword(string context, string linkUrl)
    {
        // 1) 先从链接 URL 自身参数提取
        var fromUrl = ExtractPasswordFromUrl(linkUrl);
        if (!string.IsNullOrEmpty(fromUrl))
            return fromUrl;

        if (string.IsNullOrEmpty(context))
            return string.Empty;

        // 2) 链接附近短窗口（约 60 字符）
        var nearby = ExtractCodeNear(linkUrl, context);
        if (!string.IsNullOrEmpty(nearby))
            return nearby;

        // 3) 整段扫描各类格式
        // 3.1 天翼访问码
        if (context.Contains("cloud.189.cn", StringComparison.OrdinalIgnoreCase))
        {
            var t = TianyiAccessCodePattern.Match(context);
            if (t.Success) return t.Groups[1].Value;
        }
        // 3.2 通用提取码：提取码：xxxx
        var pwd = PasswordPattern.Match(context);
        if (pwd.Success) return pwd.Groups[1].Value;

        return string.Empty;
    }

    /// <summary>从链接自身的参数/编码中提取密码。</summary>
    private static string ExtractPasswordFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        // 天翼编码访问码
        var tianyiEncoded = TianyiEncodedAccessCodePattern.Match(Uri.UnescapeDataString(url));
        if (tianyiEncoded.Success)
            return tianyiEncoded.Groups[1].Value;

        // 115 password=
        var m115 = Pan115PasswordPattern.Match(url);
        if (m115.Success) return m115.Groups[1].Value;

        // 迅雷 ?pwd=
        var xl = XunleiPwdPattern.Match(url);
        if (xl.Success) return xl.Groups[1].Value;

        // 通用 ?pwd=
        var common = UrlPasswordPattern.Match(url);
        if (common.Success) return common.Groups[1].Value;

        return string.Empty;
    }

    /// <summary>在链接附近窗口内查找 4-6 位提取码（尤其中文"提取码：xxxx"）。</summary>
    private static string ExtractCodeNear(string linkUrl, string context)
    {
        var idx = context.IndexOf(linkUrl, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return string.Empty;

        var start = Math.Max(0, idx - 60);
        var end = Math.Min(context.Length, idx + linkUrl.Length + 60);
        var window = context[start..end];

        // 优先中文格式 提取码：xxxx
        var pwd = RegexCache.Get(@"提取码[：:]\s*([a-zA-Z0-9]{4,6})").Match(window);
        if (pwd.Success) return pwd.Groups[1].Value;

        // 其次英文 pwd:/密码:/code:
        var pwd2 = RegexCache.GetIgnoreCase(@"(?:pwd|code|密码|verify)[=：:]\s*([a-zA-Z0-9]{4,6})").Match(window);
        if (pwd2.Success) return pwd2.Groups[1].Value;

        return string.Empty;
    }

    /// <summary>
    /// 从按钮 title 或链接尾部的密码标记中提取（例如 "密码：abcd"）。
    /// </summary>
    public static string ExtractPasswordFromButtonTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var m = RegexCache.Get(@"([a-zA-Z0-9]{4,6})\s*$").Match(title.Trim());
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}

/// <summary>
/// 网盘链接候选（识别出的类型 + 标准化 URL）。
/// </summary>
public sealed class Candidate
{
    public string Type { get; set; } = CloudTypes.Others;
    public string Url { get; set; } = string.Empty;
}
