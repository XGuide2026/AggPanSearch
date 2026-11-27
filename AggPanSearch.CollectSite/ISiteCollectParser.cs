namespace AggPanSearch.CollectSite;

/// <summary>
/// 站点采集解析器。一个资源站对应一个实现，按域名注册；
/// 未匹配到专用解析器时由 <see cref="GenericNetdiskSiteParser"/> 兜底。
/// </summary>
public interface ISiteCollectParser
{
    /// <summary>解析器唯一标识名（如 "duoduoziyuanku"）。</summary>
    string Name { get; }

    /// <summary>优先级，数值越小越优先。</summary>
    int Priority { get; }

    /// <summary>是否处理该页面地址（按域名匹配，需包含子域）。</summary>
    bool CanHandle(Uri pageUri);

    /// <summary>解析页面 HTML。</summary>
    Task<SiteCollectParseResult> ParseAsync(string html, Uri pageUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析分页导航（下一页、总页数、分类入口）。默认返回空导航，
    /// 未实现该方法的解析器只能按「仅当前页」采集。
    /// </summary>
    SiteCollectNavResult ParseNav(string html, Uri pageUri)
    {
        return new SiteCollectNavResult();
    }
}

/// <summary>
/// 站点解析器注册表：按域名 + 优先级选择解析器，找不到时回退到通用解析器。
/// </summary>
public sealed class SiteCollectParserRegistry
{
    /// <summary>全局默认注册表。</summary>
    public static SiteCollectParserRegistry Instance { get; } = new();

    private readonly List<ISiteCollectParser> _parsers = new();
    private readonly ISiteCollectParser _fallback = new GenericNetdiskSiteParser();

    public SiteCollectParserRegistry()
    {
        // 已适配的站点解析器在这里注册；未注册的站点自动走通用解析器
        Register(new Parsers.DuoduoResourceSiteParser());
        Register(new Parsers.OnlinezzdSiteParser());

        // HtmlAgilityPack 版：Priority 5 小于正则版的 10，注册后优先接管对应站点；
        // 需要回滚时把对应 HAP 解析器的 Priority 改回 20。
        Register(new Parsers.DuoduoHtmlAgilityPackSiteParser());
        Register(new Parsers.OnlinezzdHtmlAgilityPackSiteParser());

        // 盘它分享：列表页卡片即含完整字段（链接/提取码/简介/图片），无需跳详情页。
        Register(new Parsers.PantaShareSiteParser());
    }

    public void Register(ISiteCollectParser parser)
    {
        if (parser == null)
        {
            return;
        }
        _parsers.RemoveAll(t => string.Equals(t.Name, parser.Name, StringComparison.OrdinalIgnoreCase));
        _parsers.Add(parser);
    }

    public void RegisterRange(IEnumerable<ISiteCollectParser> parsers)
    {
        foreach (ISiteCollectParser parser in parsers)
        {
            Register(parser);
        }
    }

    /// <summary>按页面地址选择解析器，未匹配到专用解析器时返回通用解析器。</summary>
    public ISiteCollectParser Resolve(Uri pageUri)
    {
        ISiteCollectParser? matched = _parsers
            .Where(t => t.CanHandle(pageUri))
            .OrderBy(t => t.Priority)
            .FirstOrDefault();
        return matched ?? _fallback;
    }

    public IReadOnlyList<ISiteCollectParser> All => _parsers;
}
