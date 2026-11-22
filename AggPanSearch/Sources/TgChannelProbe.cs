using System.Collections.Concurrent;
using AggPanSearch.Config;
using AggPanSearch.Util;

namespace AggPanSearch.Sources;

/// <summary>
/// 单个频道的探测结论。
/// </summary>
public enum TgProbeOutcome
{
    /// <summary>没能得出确定结论（网络异常、超时、被限流），不应据此把频道标记为不可访问。</summary>
    Unknown = 0,

    /// <summary>频道可访问（页面能打开且确实是频道页）。</summary>
    Accessible = 1,

    /// <summary>频道确定不可访问（不存在、被删除、私密、返回 404/403 等）。</summary>
    Inaccessible = 2,
}

/// <summary>
/// 单个 TG 频道的探测结果。
/// </summary>
public sealed class TgChannelProbeResult
{
    /// <summary>探测结论。</summary>
    public TgProbeOutcome Outcome { get; set; } = TgProbeOutcome.Unknown;

    /// <summary>频道是否可访问（页面可打开且确实是频道页），等价于 Outcome == Accessible。</summary>
    public bool Accessible { get; set; }

    /// <summary>最近一次发布网盘资源链接的时间（本地时间），没有则为 null。</summary>
    public DateTime? LastUpdate { get; set; }

    /// <summary>探测说明，可直接展示给后台用户（也作为「检测失败」的原因提示）。</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// TG 频道可用性探测：访问 https://t.me/s/{channel}，判断频道是否可访问，
/// 并取最近一条「含网盘链接」消息的发布时间作为最近资源更新时间。
///
/// 实现要点（避免误判「不可访问」）：
/// 1. 复用 HttpClient 连接池，不再每条频道新建 handler（新建会重复 TLS 握手，慢且容易被限流）；
/// 2. 网络异常 / 超时 / HTTP 429 / 5xx 一律重试（指数退避），仍失败则结论为 Unknown（前端显示「检测失败」，
///    不写库），只有明确的 2xx 无频道结构或 4xx 才判定为「不可访问」；
/// 3. 频道页结构判定兼容 tgme_channel_info / tgme_channel_history / tgme_widget_message / og:title。
/// </summary>
public static class TgChannelProbe
{
    /// <summary>频道首页 HTML 读取上限（t.me 频道页通常数百 KB 到数 MB）。</summary>
    private const int MaxHtmlBytes = 4 * 1024 * 1024;

    /// <summary>最多尝试次数（含首次）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>共享 HttpUtil 缓存：key = 代理 + 超时，避免每条频道都新建连接池。</summary>
    private static readonly ConcurrentDictionary<string, HttpUtil> Clients = new();

    /// <summary>
    /// 探测单个频道。
    /// </summary>
    /// <param name="channel">频道名（t.me/s/ 后面的部分）。</param>
    /// <param name="config">AggPanSearch 配置，主要用其中的代理与超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<TgChannelProbeResult> ProbeAsync(string? channel, AggPanSearchConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var result = new TgChannelProbeResult();
        string name = (channel ?? string.Empty).Trim().TrimStart('@').Trim('/');
        if (name.Length == 0)
        {
            result.Outcome = TgProbeOutcome.Inaccessible;
            result.Message = "频道名为空";
            return result;
        }

        config ??= new AggPanSearchConfig();
        double timeoutSeconds = config.TgRequestTimeoutSeconds > 0 ? config.TgRequestTimeoutSeconds + 5 : 20;
        HttpUtil http = GetClient(config.Proxy, timeoutSeconds);
        string url = $"https://t.me/s/{Uri.EscapeDataString(name)}";

        HttpFetchResult fetch = new() { StatusCode = 0, Error = "未执行请求" };
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fetch = await http.GetStringResultAsync(url, cancellationToken, MaxHtmlBytes).ConfigureAwait(false);

            // 成功，或属于「确定不可访问」的 4xx（403/404/410 等），无需重试
            if (fetch.IsSuccess || IsDefinitiveFailure(fetch.StatusCode))
            {
                break;
            }
            // 限流/服务端错误/网络异常：退避后重试
            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!fetch.IsSuccess)
        {
            if (IsDefinitiveFailure(fetch.StatusCode))
            {
                result.Outcome = TgProbeOutcome.Inaccessible;
                result.Message = $"频道不存在或不可访问(HTTP {fetch.StatusCode})";
                return result;
            }

            result.Outcome = TgProbeOutcome.Unknown;
            if (fetch.StatusCode == 429)
            {
                result.Message = "检测失败：请求被限流(429)，请稍后重试";
            }
            else if (fetch.StatusCode > 0)
            {
                result.Message = $"检测失败(HTTP {fetch.StatusCode})，请稍后重试";
            }
            else
            {
                result.Message = "检测失败：" + (string.IsNullOrWhiteSpace(fetch.Error) ? "网络异常" : fetch.Error);
            }
            return result;
        }

        string html = fetch.Content ?? string.Empty;

        // t.me 对不存在的频道也返回 200，用频道信息区/消息块区分「真频道页」与「空壳页」。
        // 注意：部分频道（受限预览/需登录才能看 s/ 预览页）返回的是普通频道页 tgme_page，
        // 没有 channel_history / widget_message，但确实是存在的频道，浏览器也能打开，
        // 因此这里额外用「页面里出现该频道名」作为兜底判定，避免误判成「不可访问」。
        bool hasChannelMarkers = html.Contains("tgme_channel_info", StringComparison.OrdinalIgnoreCase)
            || html.Contains("tgme_channel_history", StringComparison.OrdinalIgnoreCase)
            || html.Contains("tgme_widget_message", StringComparison.OrdinalIgnoreCase);
        bool hasGenericChannelPage = (html.Contains("tgme_page_title", StringComparison.OrdinalIgnoreCase)
                || html.Contains("tgme_page_extra", StringComparison.OrdinalIgnoreCase))
            && html.Contains(name, StringComparison.OrdinalIgnoreCase);
        bool looksLikeChannel = hasChannelMarkers || hasGenericChannelPage;
        if (!looksLikeChannel)
        {
            result.Outcome = TgProbeOutcome.Inaccessible;
            result.Message = html.Length < 2048
                ? "频道不存在或不可访问（页面内容异常，可能是网络被劫持或请求被拦截）"
                : "频道不存在或不可访问";
            return result;
        }

        result.Outcome = TgProbeOutcome.Accessible;
        result.Accessible = true;

        var parsed = TgParser.Parse(html, name);
        DateTime latestUtc = parsed.Results
            .Where(t => t.Datetime != default)
            .Select(t => t.Datetime)
            .DefaultIfEmpty(default)
            .Max();

        if (latestUtc == default)
        {
            result.Message = parsed.Status == TgParser.ParseStatus.NoMessages
                ? "频道可访问，但暂无消息"
                : "频道可访问，但最近没有网盘资源";
            return result;
        }

        // TgParser 用 AdjustToUniversal 解析（UTC），这里转成本地时间再入库/展示
        result.LastUpdate = DateTime.SpecifyKind(latestUtc, DateTimeKind.Utc).ToLocalTime();
        result.Message = "频道可访问";
        return result;
    }

    /// <summary>
    /// 该状态码是否属于「足够确定、重试也没用」的失败（除 429 外的 4xx）。
    /// </summary>
    private static bool IsDefinitiveFailure(int statusCode)
    {
        return statusCode >= 400 && statusCode < 500 && statusCode != 408 && statusCode != 429;
    }

    /// <summary>
    /// 取（按代理与超时区分的）共享 HttpUtil；同一配置下复用连接池。
    /// </summary>
    private static HttpUtil GetClient(string? proxy, double timeoutSeconds)
    {
        string key = (proxy ?? string.Empty) + "|" + timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Clients.GetOrAdd(key, _ => new HttpUtil(proxy, timeoutSeconds, 8));
    }
}
