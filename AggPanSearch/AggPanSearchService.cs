using AggPanSearch.Config;
using AggPanSearch.Engine;
using AggPanSearch.Models;
using AggPanSearch.Sources;
using AggPanSearch.Sources.Plugins;

namespace AggPanSearch;

/// <summary>
/// AggPanSearch 网盘搜索对外门面：统一封装配置、引擎与插件注册，供上层直接调用。
/// </summary>
public sealed class AggPanSearchService : IDisposable
{
    private readonly SearchEngine _engine;
    private readonly AggPanSearchConfig _config;

    /// <summary>底层搜索引擎（可用于精细控制缓存等）。</summary>
    public SearchEngine Engine => _engine;

    /// <summary>是否启用缓存。</summary>
    public bool UseCache
    {
        get => _engine.UseCache;
        set => _engine.UseCache = value;
    }

    /// <summary>全局插件仓库的便捷访问。</summary>
    public PluginRegistry Plugins => PluginRegistry.Instance;

    public AggPanSearchService(AggPanSearchConfig config)
    {
        _config = config;
        _engine = new SearchEngine(config);
    }

    /// <summary>
    /// 执行一次搜索。默认仅返回按网盘类型合并的结果。
    /// </summary>
    /// <param name="keyword">关键词（必填）。</param>
    /// <param name="configure">可选：配置更多搜索参数。</param>
    public Task<SearchResponse> SearchAsync(string keyword, Action<SearchRequestBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var request = new SearchRequest { Keyword = keyword };
        configure?.Invoke(new SearchRequestBuilder(request));
        return _engine.SearchAsync(request, cancellationToken);
    }

    /// <summary>直接以请求对象执行搜索（可完全控制）。</summary>
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        => _engine.SearchAsync(request, cancellationToken);

    /// <summary>清空缓存。</summary>
    public void ClearCache() => _engine.ClearCache();

    /// <summary>
    /// 便捷工厂：创建默认服务并注册演示/示例插件。
    /// </summary>
    public static AggPanSearchService CreateDefault(Action<AggPanSearchConfig>? configure = null)
    {
        var config = new AggPanSearchConfig();
        configure?.Invoke(config);

        var service = new AggPanSearchService(config);
        // 注册演示插件，便于开箱即用验证
        service.Plugins.Register(new MockSearchPlugin());
        return service;
    }

    /// <summary>
    /// 注册一个通用的 JSON 网盘搜索插件。
    /// </summary>
    /// <param name="name">插件名，对应 config.EnabledPlugins 中的名字。</param>
    /// <param name="baseUrl">第三方搜索接口地址。</param>
    public void AddJsonNetdiskPlugin(string name, string baseUrl, int level = 2, string queryParam = "q")
    {
        Plugins.Register(new GenericJsonNetdiskPlugin(name, baseUrl, level, queryParam, _config.Proxy));
    }

    public void Dispose()
    {
        // SearchEngine 与插件自身的 HttpClient 通过 GC 管理；这里预留释放钩子
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 链式构建搜索请求。
/// </summary>
public sealed class SearchRequestBuilder
{
    private readonly SearchRequest _request;
    internal SearchRequestBuilder(SearchRequest request) => _request = request;

    /// <summary>指定搜索的频道列表。</summary>
    public SearchRequestBuilder Channels(params string[] channels) { _request.Channels = channels; return this; }

    /// <summary>指定搜索的插件列表（空则全部启用插件）。</summary>
    public SearchRequestBuilder Plugins(params string[] plugins) { _request.Plugins = plugins; return this; }

    /// <summary>指定返回的网盘类型。</summary>
    public SearchRequestBuilder CloudTypes(params string[] types) { _request.CloudTypes = types; return this; }

    /// <summary>并发数。</summary>
    public SearchRequestBuilder Concurrency(int value) { _request.Concurrency = value; return this; }

    /// <summary>强制刷新，跳过缓存。</summary>
    public SearchRequestBuilder Refresh() { _request.ForceRefresh = true; return this; }

    /// <summary>结果类型：all / results / merge，默认 merge。</summary>
    public SearchRequestBuilder ResultType(string value) { _request.ResultType = value; return this; }

    /// <summary>数据来源：all / tg / plugin，默认 all。</summary>
    public SearchRequestBuilder Source(string value) { _request.SourceType = value; return this; }

    /// <summary>设置过滤条件。</summary>
    public SearchRequestBuilder Filter(SearchFilter filter) { _request.Filter = filter; return this; }

    /// <summary>设置扩展参数。</summary>
    public SearchRequestBuilder Ext(IDictionary<string, object> ext) { _request.Ext = ext; return this; }
}