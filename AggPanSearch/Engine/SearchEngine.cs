using System.Diagnostics;
using AggPanSearch.Cache;
using AggPanSearch.Config;
using AggPanSearch.Models;
using AggPanSearch.Sources;

namespace AggPanSearch.Engine;

/// <summary>
/// AggPanSearch 搜索核心引擎。
/// 对应 Go 项目 service/search_service.go 的 Search 方法：
/// 并行执行 TG 频道搜索与插件搜索 → 归并 → 排序 → 分组 → 过滤 → 构造响应。
/// 支持二级结果缓存（对应缓存集成逻辑）。
/// </summary>
public sealed class SearchEngine
{
    private readonly AggPanSearchConfig _config;
    private readonly IReadOnlyList<string> _defaultChannels;
    private readonly TwoLevelCache<SearchResponse>? _cache;
    private TgChannelSource? _sharedTgSource;
    private readonly object _tgSourceLock = new();

    public bool UseCache { get; set; } = true;

    public SearchEngine(AggPanSearchConfig config)
    {
        _config = config;
        _defaultChannels = config.GetChannelList();
        if (config.CacheTtlMinutes > 0)
        {
            _cache = new TwoLevelCache<SearchResponse>(
                memoryMaxEntries: Math.Max(200, config.CacheMaxSizeMb * 4),
                diskEnabled: config.CachePath is not null && config.CachePath.Length > 0,
                diskDir: config.CachePath);
        }
    }

    /// <summary>清空缓存。</summary>
    public void ClearCache() => _cache?.Clear();

    /// <summary>
    /// 执行一次搜索。
    /// </summary>
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Keyword))
            throw new ArgumentException("搜索关键词不能为空", nameof(request));

        var keyword = request.Keyword.Trim();
        var sourceType = request.NormalizedSourceType();
        var resultType = request.NormalizedResultType();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 结果类型影响缓存键；merge 类型缓存最实用
        var cacheKey = BuildCacheKey(keyword, sourceType, resultType, request.CloudTypes, request.Plugins, request.Filter);
        if (UseCache && !request.ForceRefresh && _cache is not null &&
            _cache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            // 标记缓存命中，供上层（搜索日志）记录"是否命中缓存"
            cached.FromCache = true;
            return cached;
        }

        var concurrency = request.Concurrency > 0 ? request.Concurrency : _config.Concurrency;
        if (concurrency <= 0)
            concurrency = Math.Max(2, Math.Min(16, Environment.ProcessorCount * 2));

        var useTg = sourceType is "all" or "tg";
        var usePlugin = sourceType is "all" or "plugin" && _config.AsyncPluginEnabled;

        // 最快响应窗口：窗口到期后返回已收集结果快照，不等最慢的频道/插件（对齐 Go 原版异步响应机制）。
        // <=0 表示关闭（等待全部完成）。
        var fastResponseWindow = _config.AsyncResponseTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(0.5, _config.AsyncResponseTimeoutSeconds))
            : (TimeSpan?)null;
        _config.LogDebug($"[Engine] 最快响应窗口={fastResponseWindow?.TotalSeconds.ToString("0.#") ?? "关闭"}s，并发={concurrency}");

        // 并行：TG + 插件
        var tgTask = useTg ? Task.Run(() => SearchTgAsync(keyword, request.Channels, concurrency, cancellationToken, fastResponseWindow)) : Task.FromResult(new List<SearchResult>());
        var pluginTask = usePlugin ? Task.Run(() => SearchPluginsAsync(keyword, request.Plugins, request.Ext, concurrency, cancellationToken, fastResponseWindow)) : Task.FromResult(new List<SearchResult>());

        await Task.WhenAll(tgTask, pluginTask).ConfigureAwait(false);
        _config.LogDebug($"[Engine] 并行阶段完成 @ {sw.ElapsedMilliseconds}ms ({ThreadPoolStat()})");

        var tgResults = await tgTask.ConfigureAwait(false);
        var pluginResults = await pluginTask.ConfigureAwait(false);
        _config.LogDebug($"[Engine] 归并前 @ {sw.ElapsedMilliseconds}ms (tg={tgResults.Count} 插件={pluginResults.Count})");

        // 合并
        var swOp = Stopwatch.StartNew();
        var merged = ResultMerger.Merge(tgResults.Concat(pluginResults));
        swOp.Stop();
        _config.LogDebug($"[Engine] 归并完成 @ {sw.ElapsedMilliseconds}ms (merged={merged.Count} 调用={swOp.ElapsedMilliseconds}ms {GcStat()})");

        // 过滤（关键词 include/exclude + 网盘类型）
        swOp.Restart();
        merged = ResultMerger.Filter(merged, request.Filter, request.CloudTypes);
        swOp.Stop();
        _config.LogDebug($"[Engine] 过滤完成 @ {sw.ElapsedMilliseconds}ms (results={merged.Count} 调用={swOp.ElapsedMilliseconds}ms)");

        // 排序
        swOp.Restart();
        ResultSorter.Sort(merged);
        swOp.Stop();
        _config.LogDebug($"[Engine] 排序完成 @ {sw.ElapsedMilliseconds}ms (调用={swOp.ElapsedMilliseconds}ms)");

        var mergedByType = resultType is "all" or "merge" ? TypeMerger.MergeByType(merged, request.Keyword) : null;
        _config.LogDebug($"[Engine] 分组完成 @ {sw.ElapsedMilliseconds}ms (results={merged.Count} {GcStat()})");

        var response = new SearchResponse
        {
            // 对齐 Go 原版口径：merge 模式 Total = 链接级去重后的链接数；results 模式 Total = 帖子数
            Total = mergedByType is not null
                ? mergedByType.Values.Sum(v => v.Count)
                : merged.Count,
            Results = resultType is "all" or "results" ? merged : null,
            MergedByType = mergedByType,
        };

        // 缓存写入：仅缓存有效结果。空结果（网络失败/确实无资源）不缓存，
        // 否则代理恢复或资源上架后，同一关键词仍会被空缓存挡住 TTL 时长
        if (UseCache && _cache is not null && response.Total > 0)
        {
            var ttl = TimeSpan.FromMinutes(Math.Max(1, _config.CacheTtlMinutes));
            _cache.Set(cacheKey, response, ttl);
            _config.LogDebug($"[Engine] 缓存写入完成 @ {sw.ElapsedMilliseconds}ms");
        }

        sw.Stop();
        _config.LogDebug($"[Engine] 搜索完成 keyword={keyword} total={response.Total} 耗时={sw.ElapsedMilliseconds}ms ({GcStat()})");
        return response;
    }

    private static string GcStat()
        => $"gc2={GC.CollectionCount(2)} gen0={GC.CollectionCount(0)} mem={GC.GetTotalMemory(false) >> 20}MB";

    private static string ThreadPoolStat()
    {
        ThreadPool.GetAvailableThreads(out var avail, out _);
        ThreadPool.GetMaxThreads(out var max, out _);
        var busy = max - avail;
        return $"线程池={busy}/{max} 在用 {GcStat()}";
    }

    private static string BuildCacheKey(string keyword, string sourceType, string resultType,
        IReadOnlyList<string>? cloudTypes, IReadOnlyList<string>? plugins, SearchFilter? filter)
    {
        var filterSig = filter is null
            ? ""
            : $"{string.Join(",", filter.Include ?? Array.Empty<string>())}|{string.Join(",", filter.Exclude ?? Array.Empty<string>())}";
        var k = $"{keyword}|{sourceType}|{resultType}|{string.Join(",", cloudTypes ?? Array.Empty<string>())}|{string.Join(",", plugins ?? Array.Empty<string>())}|{filterSig}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(k)))[..32];
    }

    /// <summary>
    /// 执行 TG 频道搜索。
    /// </summary>
    private async Task<List<SearchResult>> SearchTgAsync(string keyword, IReadOnlyList<string>? channels, int concurrency,
        CancellationToken cancellationToken, TimeSpan? fastResponseWindow = null)
    {
        var channelList = channels is { Count: > 0 } ? channels : _defaultChannels;
        if (channelList.Count == 0)
            return new List<SearchResult>();

        // 复用共享 TgChannelSource（内部 HttpClient/连接池跨请求复用，避免每次重建；
        // 也不在窗口期 Dispose，避免中断仍在途的后台请求）
        var source = GetTgSource();

        var swTg = Stopwatch.StartNew();
        _config.LogDebug($"[TG] 启动搜索，{channelList.Count} 个频道，窗口={fastResponseWindow?.TotalSeconds.ToString("0.#") ?? "关闭"}s");
        var results = await WorkerPool.ExecuteAsync(
            channelList,
            Math.Max(1, concurrency),
            async (channel, ct) =>
            {
                var r = await source.SearchAsync(channel, keyword, ct).ConfigureAwait(false);
                return (r.Results, 0d);
            },
            cancellationToken,
            fastResponseWindow,
            _config.LogDebug).ConfigureAwait(false);

        var done = results.Count(r => r.Completed);
        var hit = results.Where(r => r.Error is null && r.Output is not null).Sum(r => r.Output!.Count);
        var errors = results.Where(r => r.Error is not null).ToList();
        var errSummary = errors
            .GroupBy(r => r.Error!.GetType().Name)
            .Select(g => $"{g.Key}×{g.Count()}")
            .ToArray();
        var firstErr = errors.FirstOrDefault().Error;
        var hint = errors.Count == channelList.Count
            ? "，全部频道请求失败（详见错误示例，排查网络/接口）"
            : "";
        _config.LogDebug($"[TG] 窗口内完成 {done}/{channelList.Count} 个频道，命中 {hit} 条" +
            (errSummary.Length > 0 ? $"，错误=[{string.Join(",", errSummary)}]" : "") +
            (firstErr is not null ? $"，示例：{firstErr.GetType().Name}: {Truncate(firstErr.Message, 120)}" : "") +
            hint);
        _config.LogDebug($"[TG] WorkerPool 返回 @ {swTg.ElapsedMilliseconds}ms");

        return results.Where(r => r.Error is null && r.Output is not null)
            .SelectMany(r => r.Output!)
            .ToList();
    }

    /// <summary>
    /// 执行插件搜索：Filter 出目标插件，并发执行，合并结果。
    /// </summary>
    private async Task<List<SearchResult>> SearchPluginsAsync(string keyword, IReadOnlyList<string>? pluginNames,
        IDictionary<string, object>? ext, int concurrency, CancellationToken cancellationToken, TimeSpan? fastResponseWindow = null)
    {
        if (PluginRegistry.Instance is null || PluginRegistry.Instance.Count == 0)
            return new List<SearchResult>();

        var plugins = PluginRegistry.Instance.Filter(pluginNames?.ToArray());
        if (plugins.Count == 0)
            return new List<SearchResult>();

        var swPlugin = Stopwatch.StartNew();
        _config.LogDebug($"[Plugin] 启动搜索，{plugins.Count} 个插件");
        var results = await WorkerPool.ExecuteAsync(
            plugins,
            Math.Max(1, concurrency),
            async (plugin, ct) =>
            {
                var r = await plugin.SearchAsync(keyword, ext, ct).ConfigureAwait(false);
                return (r.Results, 0d);
            },
            cancellationToken,
            fastResponseWindow,
            _config.LogDebug).ConfigureAwait(false);

        _config.LogDebug($"[Plugin] WorkerPool 返回 @ {swPlugin.ElapsedMilliseconds}ms");
        return results.Where(r => r.Error is null && r.Output is not null)
            .SelectMany(r => r.Output!)
            .ToList();
    }

    /// <summary>获取（懒创建）共享的 TG 数据源，复用其 HttpClient/连接池。</summary>
    private TgChannelSource GetTgSource()
    {
        if (_sharedTgSource is null)
        {
            lock (_tgSourceLock)
            {
                _sharedTgSource ??= new TgChannelSource(_config);
            }
        }
        return _sharedTgSource;
    }

    /// <summary>截断长文本用于日志输出。</summary>
    private static string Truncate(string? s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}