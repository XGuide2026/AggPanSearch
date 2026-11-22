using System.Globalization;
using System.Text.RegularExpressions;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 分页链接模板（含 {page} 占位符）的唯一实现：校验、按页码构建地址、由列表页地址推导起始页码。
/// 视图预检、站点配置校验、任务启动校验、引擎翻页都以此为唯一口径。
/// </summary>
public sealed class SiteCollectPageTemplate
{
    /// <summary>分页链接模板中的页码占位符。</summary>
    public const string Placeholder = "{page}";

    public const int MinPageNo = 1;

    /// <summary>起始页码上限，避免异常输入导致长时间翻页。</summary>
    public const int MaxPageNo = 100000;

    private readonly bool _canNormalizeSlash;

    private SiteCollectPageTemplate(string template, string prefix, string suffix, bool canNormalizeSlash)
    {
        Template = template;
        Prefix = prefix;
        Suffix = suffix;
        _canNormalizeSlash = canNormalizeSlash;
    }

    /// <summary>去掉首尾空白后的原始模板。</summary>
    public string Template { get; }

    /// <summary>占位符之前的部分（含协议与主机）。</summary>
    public string Prefix { get; }

    /// <summary>占位符之后的部分（原样，未裁剪斜杠）。</summary>
    public string Suffix { get; }

    /// <summary>
    /// 是否允许自动补 / 去末尾斜杠：{page} 为最后一段路径、模板不含查询串，
    /// 且前缀以 / 结尾（否则补斜杠会把数字拼进上一段路径）。
    /// </summary>
    public bool CanNormalizeSlash
    {
        get { return _canNormalizeSlash; }
    }

    /// <summary>示例地址（第 1 页），用于提示文案，保证提示里的地址与实际请求一致。</summary>
    public string ExampleUrl
    {
        get { return BuildUrl(1); }
    }

    /// <summary>校验并解析模板：必须含 {page} 占位符，替换后须为 http/https 地址。</summary>
    public static bool TryParse(string? template, out SiteCollectPageTemplate parsed, out string error)
    {
        parsed = null!;
        error = string.Empty;

        string value = (template ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "分页链接为空，无法进行分页列表采集";
            return false;
        }

        int index = value.IndexOf(Placeholder, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            error = "分页链接需包含 {page} 占位符，如 https://example.com/page/{page}";
            return false;
        }

        string prefix = value.Substring(0, index);
        string suffix = value.Substring(index + Placeholder.Length);

        // {page} 为最后一段路径且没有查询串时才做斜杠正则化，避免误改 ?page= 之类的模板。
        bool lastSegment = !value.Contains('?') && suffix.Trim().Trim('/').Length == 0;
        bool canNormalize = lastSegment && prefix.EndsWith("/", StringComparison.Ordinal);

        string probe = BuildUrl(prefix, suffix, 1, canNormalize ? true : false, canNormalize);
        if (!SiteCollectUrlRules.IsHttpUrl(probe))
        {
            error = "分页链接需以 http:// 或 https:// 开头";
            return false;
        }

        parsed = new SiteCollectPageTemplate(value, prefix, suffix, canNormalize);
        return true;
    }

    /// <summary>
    /// 按页码生成地址。可就地纠正的模板默认带末尾斜杠（实测多数站点缺斜杠会 404），
    /// 站点实际不认斜杠时由引擎首屏探测回退到另一种写法。
    /// </summary>
    public string BuildUrl(int pageNo)
    {
        return BuildUrl(pageNo, true);
    }

    /// <summary>
    /// 按页码生成地址，并指定是否带末尾斜杠（仅对 <see cref="CanNormalizeSlash"/> 的模板生效）。
    /// </summary>
    public string BuildUrl(int pageNo, bool trailingSlash)
    {
        return BuildUrl(Prefix, Suffix, pageNo, trailingSlash, _canNormalizeSlash, Template);
    }

    /// <summary>
    /// 由所填列表页地址推导起始页码：
    /// 地址为空或与站点地址相同（忽略协议、www.、末尾斜杠）时从第 1 页开始；
    /// 否则把模板按 {page} 切成前缀/后缀，前缀后代为数字捕获组，再整体锚定匹配地址并取出页码。
    /// 匹配时容忍 http/https、有没有 www.、末尾斜杠多少、以及多余的 #锚点 / 查询串（模板本身不含查询串时）。
    /// </summary>
    public bool TryResolvePageNo(string? listUrl, string? siteUrl, out int pageNo, out string error)
    {
        pageNo = MinPageNo;
        error = string.Empty;

        string url = (listUrl ?? string.Empty).Trim();
        if (url.Length == 0 || SiteCollectUrlRules.IsSameUrl(url, siteUrl))
        {
            return true;
        }

        // 注意：不能先对整条模板 Regex.Escape 再替换占位符——Escape 只转义 { 不转义 }，
        // 替换目标串与转义结果不一致会导致替换失效。这里按占位符切分后分别转义。
        string pattern = "^"
            + BuildPrefixPattern(Prefix)
            + @"(\d+)"
            + BuildSuffixPattern(Suffix)
            + "$";

        string target = BuildMatchTarget(url, !Template.Contains('?'));
        Match match = Regex.Match(target, pattern, RegexOptions.IgnoreCase);
        // 号码段允许前导零，取出后按整数校验范围。
        if (!match.Success || match.Groups.Count < 2
            || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < MinPageNo || parsed > MaxPageNo)
        {
            error = "列表页地址与分页链接模板不匹配，无法识别页码，示例：" + ExampleUrl;
            return false;
        }

        pageNo = parsed;
        return true;
    }

    private static string BuildUrl(string prefix, string suffix, int pageNo, bool trailingSlash, bool canNormalize, string? template = null)
    {
        string number = pageNo.ToString(CultureInfo.InvariantCulture);
        if (!canNormalize)
        {
            return (template ?? prefix + Placeholder + suffix)
                .Replace(Placeholder, number, StringComparison.OrdinalIgnoreCase);
        }
        return prefix + number + (trailingSlash ? "/" : string.Empty);
    }

    /// <summary>
    /// 构建占位符之前部分的匹配模式：去掉协议后转义（协议差异不影响匹配），
    /// 若主机名以 www. 开头则做成可选，带不带 www 都能命中；
    /// 模板写成相对路径（不带协议）时允许任意主机前缀。
    /// </summary>
    private static string BuildPrefixPattern(string prefix)
    {
        string value = (prefix ?? string.Empty).Trim();
        bool hasScheme = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        string escaped = Regex.Escape(SiteCollectUrlRules.StripScheme(value));
        if (escaped.StartsWith("www\\.", StringComparison.OrdinalIgnoreCase))
        {
            escaped = "(?:www\\.)?" + escaped.Substring("www\\.".Length);
        }
        if (!hasScheme && escaped.StartsWith("/", StringComparison.Ordinal))
        {
            escaped = "(?:[^/]*?)" + escaped;
        }
        return escaped;
    }

    /// <summary>
    /// 构建占位符之后部分的匹配模式：去掉末尾斜杠后转义，再追加 /* 让末尾斜杠可有可无。
    /// </summary>
    private static string BuildSuffixPattern(string suffix)
    {
        string escaped = Regex.Escape((suffix ?? string.Empty).Trim().TrimEnd('/'));
        return escaped + "/*";
    }

    /// <summary>
    /// 预处理待匹配地址：去协议、去 # 锚点；模板不含查询串时再去掉多余的查询参数。
    /// </summary>
    private static string BuildMatchTarget(string url, bool dropQuery)
    {
        string value = SiteCollectUrlRules.StripScheme(url);

        int fragmentIndex = value.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            value = value.Substring(0, fragmentIndex);
        }

        if (!dropQuery)
        {
            return value.Trim();
        }

        int queryIndex = value.IndexOf('?');
        if (queryIndex >= 0)
        {
            value = value.Substring(0, queryIndex);
        }

        return value.Trim();
    }
}
