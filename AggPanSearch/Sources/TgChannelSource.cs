using System.Text;
using AggPanSearch.Config;
using AggPanSearch.Models;
using AggPanSearch.Util;

namespace AggPanSearch.Sources;

/// <summary>
/// TG 频道搜索数据源：抓取 https://t.me/s/{channel}?q={keyword} 页面并解析。
/// 对应 Go 项目的 service.searchTG 逻辑。
///
/// 代理说明：
///   默认（未配置 Proxy）直接访问 t.me —— 若你的网络本身可直连 Telegram 则无需任何设置。
///   仅当直连不可达时才需要在 AggPanSearchConfig.Proxy 中配置 SOCKS5/HTTP 代理。
/// </summary>
public sealed class TgChannelSource : IDisposable
{
    /// <summary>单个频道搜索页 HTML 读取上限（t.me 搜索页通常数百 KB，限制可避免大对象 GC 停顿）。</summary>
    private const int MaxHtmlBytes = 1024 * 1024;

    private readonly HttpUtil _http;
    private readonly AggPanSearchConfig _config;
    private readonly bool _useSearchQuery;
    private readonly string _probeChannel;
    private bool? _reachable;

    /// <summary>
    /// t.me 是否可达。未探测过时返回 true（假定可达，实际以搜索请求结果为准）。
    /// </summary>
    public bool IsReachable => _reachable ?? true;

    public TgChannelSource(AggPanSearchConfig config, bool useSearchQuery = true)
    {
        _config = config;
        _useSearchQuery = useSearchQuery;
        // Proxy 为 null/空时自动直连，配置了则走代理。
        _http = new HttpUtil(config.Proxy, config.TgRequestTimeoutSeconds + 5);
        // 探测频道：优先使用配置的默认频道，无配置时退回内置默认，避免硬编码依赖特定频道名。
        _probeChannel = config.GetChannelList().FirstOrDefault() ?? "tgsearchers7";
    }

    /// <summary>
    /// 进行一次轻量可达性探测（访问 t.me 首页判断直连/代理是否连通）。
    /// 结果会缓存到本实例的 IsReachable。
    /// </summary>
    /// <returns>true 表示 t.me 可达。</returns>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_reachable is not null)
            return _reachable.Value;

        bool ok;
        try
        {
            // 只读响应头判断可达性，不下载频道首页大 HTML，避免拖慢冷搜索
            ok = await _http.IsReachableAsync($"https://t.me/s/{_probeChannel}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ok = false;
        }

        _reachable = ok;
        _config.LogDebug(ok
            ? "[TG] t.me 可达（当前为" + (string.IsNullOrEmpty(_config.Proxy) ? "直连" : $"代理 {_config.Proxy}") + "）"
            : "[TG] t.me 不可达" + (string.IsNullOrEmpty(_config.Proxy) ? "，如需访问可在 AggPanSearchConfig.Proxy 配置代理" : "，检查代理配置"));
        return ok;
    }

    /// <summary>
    /// 搜索单个频道。
    /// </summary>
    /// <param name="channel">频道名，如 tgsearchers7。</param>
    /// <param name="keyword">关键词。</param>
    public async Task<PluginSearchResult> SearchAsync(string channel, string keyword,
        CancellationToken cancellationToken = default)
    {
        var url = BuildQueryUrl(channel, keyword);
        // 不再单独探测：直接用搜索请求结果判断可达性（探测=一次额外 RTT）
        var html = await _http.GetStringAsync(url, cancellationToken, MaxHtmlBytes).ConfigureAwait(false);
        if (html is null)
        {
            _reachable = false;
            // 非 200：视为失败（限流/404 不等同于"无匹配内容"）
            return new PluginSearchResult
            {
                Source = $"tg:{channel}",
                IsFinal = false,
                Message = "频道请求失败",
                Timestamp = DateTime.UtcNow,
            };
        }

        // 能拿到 HTML 说明网络可达
        _reachable = true;

        // 窗口已过期（请求被取消）：不再做昂贵的正则解析，避免未完成任务
        // 在后台抢占线程池 CPU/线程，拖慢主线程的归并/排序等同步段
        if (cancellationToken.IsCancellationRequested)
        {
            return new PluginSearchResult
            {
                Source = $"tg:{channel}",
                IsFinal = false,
                Message = "窗口已过期",
                Timestamp = DateTime.UtcNow,
            };
        }

        var swParse = System.Diagnostics.Stopwatch.StartNew();
        var parsed = TgParser.Parse(html, channel);
        _config.LogDebug($"[TG] Parse {channel} 耗时={swParse.ElapsedMilliseconds}ms html={html.Length}B 结果={parsed.Results.Count}");
        return new PluginSearchResult
        {
            Source = $"tg:{channel}",
            IsFinal = true,
            Timestamp = DateTime.UtcNow,
            Results = parsed.Results,
            Message = parsed.Status.ToString(),
        };
    }

    private string BuildQueryUrl(string channel, string keyword)
    {
        var safeChannel = Uri.EscapeDataString(channel);
        if (_useSearchQuery && !string.IsNullOrEmpty(keyword))
            return $"https://t.me/s/{safeChannel}?q={Uri.EscapeDataString(keyword)}";
        return $"https://t.me/s/{safeChannel}";
    }

    /// <summary>释放底层 HttpClient。</summary>
    public void Dispose() => _http.Dispose();
}