using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>一次页面抓取的结果。</summary>
public sealed class PageFetchResult
{
    public bool Success { get; init; }

    /// <summary>HTTP 状态码，0 表示请求未拿到响应（网络异常/超时）。</summary>
    public int StatusCode { get; init; }

    public string Html { get; init; } = string.Empty;

    /// <summary>失败原因（中文，可直接提示给用户）。</summary>
    public string Error { get; init; } = string.Empty;

    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>失败诊断摘要（实际地址、关键响应头、响应片段），仅失败时有值。</summary>
    public string Diagnostic { get; init; } = string.Empty;

    /// <summary>失败响应体原文（限长），用于识别站点安全盾校验页；成功响应为空。</summary>
    internal string ResponseBody { get; init; } = string.Empty;
}

/// <summary>
/// 采集用的异步页面抓取器：连接池复用、UA 伪装、gzip/br 解压、20 秒超时、8MB 限长、编码自动识别、
/// 内网地址拦截（避免 SSRF）。
/// </summary>
public static class HtmlPageFetcher
{
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>失败响应体最多读取的字节数（诊断 + 安全盾校验页识别，校验页不足 2KB）。</summary>
    private const int DiagnosticBodyBytes = 64 * 1024;

    /// <summary>诊断片段的字符上限，避免错误提示过长。</summary>
    private const int DiagnosticMaxLength = 220;

    /// <summary>安全盾校验后的最大重放次数。</summary>
    private const int ChallengeMaxAttempts = 2;

    /// <summary>安全盾校验重放前的间隔，避免瞬时高频触发站点限流。</summary>
    private const int ChallengeRetryDelayMs = 300;

    /// <summary>等待其他请求完成安全盾校验的最长时间，超时则直接重放，避免任务被长时间挂起。</summary>
    private const int ChallengeLockWaitMs = 10 * 1000;

    /// <summary>同一站点校验成功后的复用窗口，窗口内其他请求直接重放、不再解析校验页。</summary>
    private static readonly TimeSpan ChallengeCooldown = TimeSpan.FromSeconds(30);

    /// <summary>串行化安全盾校验，避免详情页并发抓取同时 403 时放大访问频率。</summary>
    private static readonly SemaphoreSlim ChallengeLock = new SemaphoreSlim(1, 1);

    /// <summary>站点主机 → 最近一次校验成功时间（UTC）。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> ChallengePassedAt =
        new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认浏览器 UA。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>403/429 兜底重试时使用的备选 UA（Edge 桌面 / Safari 移动端）。</summary>
    public static readonly string[] FallbackUserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
    };

    /// <summary>采集会话 Cookie 容器：保留站点下发的会话 Cookie 与安全盾校验令牌，供同源图片下载等复用。</summary>
    private static readonly CookieContainer CookieJar = new CookieContainer();

    private static readonly HttpClient Client = CreateClient();

    private static readonly Regex HeaderCharsetPattern = RegexCache.GetIgnoreCase(
        @"charset\s*=\s*[""']?(?<c>[a-zA-Z0-9_\-]+)");

    /// <summary>采集会话 Cookie 容器（含站点安全盾校验令牌），供图片下载等同源请求共享登录态。</summary>
    public static CookieContainer SessionCookies => CookieJar;

    static HtmlPageFetcher()
    {
        try
        {
            // .NET 默认不支持 GBK/GB2312，需要注册代码页提供程序；平台未提供时忽略，回退 UTF-8
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
        }
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 默认直连：避免继承 Windows 系统代理（如本机 Clash），代理节点出口常被站点 WAF 判定为异常来源并返回 403。
            UseProxy = false,
            // 保留会话 Cookie，兼容 WAF 下发指纹 Cookie、需要会话的站点。
            UseCookies = true,
            CookieContainer = CookieJar,
        };
        ApplyProxyFromEnvironment(handler);
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>
    /// 可选代理：设置环境变量 SITECOLLECT_PROXY（如 http://127.0.0.1:7890）后采集走该代理，未设置时一律直连。
    /// </summary>
    private static void ApplyProxyFromEnvironment(SocketsHttpHandler handler)
    {
        string? proxy = Environment.GetEnvironmentVariable("SITECOLLECT_PROXY");
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return;
        }
        try
        {
            handler.Proxy = new WebProxy(new Uri(proxy.Trim())) { BypassProxyOnLocal = false };
            handler.UseProxy = true;
        }
        catch (UriFormatException)
        {
            handler.UseProxy = false;
        }
    }

    /// <summary>构造浏览器化请求：UA、Accept 系列、Client Hints、Sec-Fetch 与 Referer。</summary>
    private static HttpRequestMessage BuildRequest(string url, string? referer, string? userAgent)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpRequestHeaders headers = request.Headers;
        headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? BrowserUserAgent : userAgent.Trim());
        headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        headers.Connection.Add("keep-alive");
        headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"131\"");
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        headers.TryAddWithoutValidation("Sec-Fetch-Site", ResolveFetchSite(url, referer));
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out Uri refererUri))
        {
            headers.Referrer = refererUri;
        }
        return request;
    }

    /// <summary>按 Referer 与目标地址的关系计算 Sec-Fetch-Site。</summary>
    private static string ResolveFetchSite(string url, string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) ||
            !Uri.TryCreate(referer, UriKind.Absolute, out Uri refererUri) ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri targetUri))
        {
            return "none";
        }
        if (refererUri.Host.Equals(targetUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return "same-origin";
        }
        return refererUri.Scheme.Equals(targetUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && refererUri.Port == targetUri.Port
            ? "same-site"
            : "cross-site";
    }

    /// <summary>
    /// 校验地址是否允许抓取：仅 http/https，且不允许指向本机或内网网段。
    /// </summary>
    public static bool IsAllowedUrl(string? url, out string error)
    {
        error = string.Empty;
        if (!SiteCollectUrlRules.TryCreateHttpUri(url, out Uri uri))
        {
            error = "采集网站地址需以 http:// 或 https:// 开头";
            return false;
        }
        string host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "不允许采集本机或内网地址";
            return false;
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && IsPrivateAddress(ip))
        {
            error = "不允许采集本机或内网地址";
            return false;
        }
        return true;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0
                || b[0] == 127;
        }
        // IPv6：::1 / fe80::/10 / fc00::/7
        byte[] v6 = ip.GetAddressBytes();
        return ip.Equals(IPAddress.IPv6Loopback)
            || (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
            || (v6[0] & 0xfe) == 0xfc;
    }

    /// <summary>抓取页面 HTML，失败时返回中文原因。</summary>
    public static Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        return FetchAsync(url, null, null, cancellationToken);
    }

    /// <summary>
    /// 抓取页面 HTML：可指定 Referer 与 User-Agent（浏览器式导航链），失败时返回中文原因与诊断摘要。
    /// 遇到站点安全盾（WTS）JS 校验拦截时自动完成校验并重放原地址，对调用方透明。
    /// </summary>
    public static async Task<PageFetchResult> FetchAsync(
        string url,
        string? referer,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedUrl(url, out string urlError))
        {
            return new PageFetchResult { Success = false, StatusCode = 0, Error = urlError };
        }

        try
        {
            PageFetchResult first = await SendOnceAsync(url, referer, userAgent, cancellationToken).ConfigureAwait(false);
            if (first.Success || first.StatusCode != 403)
            {
                return first;
            }

            // 403 且响应体是 WTS 校验页：提取令牌后重放，仍失败则回落到原始诊断错误
            PageFetchResult? passed = await TryPassWtsChallengeAsync(url, referer, userAgent, first, cancellationToken)
                .ConfigureAwait(false);
            return passed ?? first;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new PageFetchResult { Success = false, StatusCode = 0, Error = "抓取超时（超过 20 秒），请稍后重试" };
        }
        catch (HttpRequestException ex)
        {
            return new PageFetchResult { Success = false, StatusCode = 0, Error = "访问站点失败：" + ex.Message };
        }
        catch (Exception ex)
        {
            return new PageFetchResult { Success = false, StatusCode = 0, Error = "抓取页面异常：" + ex.Message };
        }
    }

    /// <summary>发送一次请求并返回结果；失败时附带限长响应体（供安全盾校验页识别）与诊断摘要。</summary>
    private static async Task<PageFetchResult> SendOnceAsync(
        string url,
        string? referer,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = BuildRequest(url, referer, userAgent);
        using HttpResponseMessage resp = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        int statusCode = (int)resp.StatusCode;
        string finalUrl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
        if (!resp.IsSuccessStatusCode)
        {
            string body = await ReadBodySafelyAsync(resp, DiagnosticBodyBytes, cancellationToken).ConfigureAwait(false);
            string diagnostic = BuildDiagnostic(resp, finalUrl, body);
            string error = BuildStatusError(statusCode);
            if (diagnostic.Length > 0)
            {
                error += "（" + diagnostic + "）";
            }
            return new PageFetchResult
            {
                Success = false,
                StatusCode = statusCode,
                Error = error,
                FinalUrl = finalUrl,
                Diagnostic = diagnostic,
                ResponseBody = body,
            };
        }

        byte[] bytes;
        using (Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            bytes = await ReadLimitedAsync(stream, MaxBytes, cancellationToken).ConfigureAwait(false);
        }

        Encoding encoding = DetectEncoding(bytes, resp.Content.Headers.ContentType?.CharSet);
        string html = DecodeSafely(bytes, encoding);

        return new PageFetchResult
        {
            Success = true,
            StatusCode = statusCode,
            Html = html,
            FinalUrl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url,
        };
    }

    /// <summary>
    /// 尝试通过站点安全盾（WTS）JS 校验：从 403 校验页取出 <c>_wtsjsk</c> 令牌写入会话 Cookie 后重放。
    /// 与校验无关的 403（真实登录限制）不做任何处理，返回 null 由调用方沿用原始错误。
    /// </summary>
    private static async Task<PageFetchResult?> TryPassWtsChallengeAsync(
        string url,
        string? referer,
        string? userAgent,
        PageFetchResult failed,
        CancellationToken cancellationToken)
    {
        if (!WtsChallenge.TryExtractToken(failed.ResponseBody, out string token))
        {
            return null;
        }

        Uri target = Uri.TryCreate(failed.FinalUrl, UriKind.Absolute, out Uri? finalUri) && finalUri != null
            ? finalUri
            : new Uri(url, UriKind.Absolute);

        if (!IsChallengeRecentlyPassed(target.Host))
        {
            bool acquired = await ChallengeLock.WaitAsync(ChallengeLockWaitMs, cancellationToken).ConfigureAwait(false);
            try
            {
                // 双检：并发抓取时可能已有其他请求完成校验，直接复用容器里的令牌
                if (acquired && !IsChallengeRecentlyPassed(target.Host) && AddChallengeCookie(target, token))
                {
                    MarkChallengePassed(target.Host);
                }
            }
            finally
            {
                if (acquired)
                {
                    ChallengeLock.Release();
                }
            }
        }

        for (int attempt = 0; attempt < ChallengeMaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ChallengeRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            PageFetchResult retry = await SendOnceAsync(url, referer, userAgent, cancellationToken).ConfigureAwait(false);
            if (retry.Success || retry.StatusCode != 403)
            {
                return retry;
            }

            // 仍是校验页：站点可能已刷新令牌，取最新的一次再试
            if (!WtsChallenge.TryExtractToken(retry.ResponseBody, out string refreshed) ||
                !AddChallengeCookie(target, refreshed))
            {
                return null;
            }
            MarkChallengePassed(target.Host);
        }

        return null;
    }

    /// <summary>把校验令牌写入共享 Cookie 容器，失败（地址/Cookie 非法）时不抛出。</summary>
    private static bool AddChallengeCookie(Uri target, string token)
    {
        try
        {
            CookieJar.Add(target, WtsChallenge.CreateCookie(target, token));
            return true;
        }
        catch (CookieException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsChallengeRecentlyPassed(string host)
    {
        return ChallengePassedAt.TryGetValue(host, out DateTime passedAt)
            && DateTime.UtcNow - passedAt < ChallengeCooldown;
    }

    private static void MarkChallengePassed(string host)
    {
        ChallengePassedAt[host] = DateTime.UtcNow;
    }

    /// <summary>读取失败响应体（限长），读取异常时返回空串，不影响主流程。</summary>
    private static async Task<string> ReadBodySafelyAsync(
        HttpResponseMessage resp,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] bytes = await ReadLimitedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            return DecodeSafely(bytes, DetectEncoding(bytes, resp.Content.Headers.ContentType?.CharSet));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildStatusError(int statusCode)
    {
        return statusCode switch
        {
            401 or 403 => $"站点拒绝访问（HTTP {statusCode}），可能需要登录或存在反爬限制",
            404 => "页面不存在（HTTP 404），请检查采集地址",
            429 => "请求过于频繁（HTTP 429），请稍后重试",
            >= 500 => $"站点服务异常（HTTP {statusCode}），请稍后重试",
            _ => $"抓取失败（HTTP {statusCode}）",
        };
    }

    /// <summary>收集失败诊断：实际地址、Server 响应头、响应片段（去标签），便于定位 403 来源。</summary>
    private static string BuildDiagnostic(HttpResponseMessage resp, string finalUrl, string body)
    {
        List<string> parts = new List<string> { "地址：" + finalUrl };

        string server = resp.Headers.Server?.ToString()?.Trim() ?? string.Empty;
        if (server.Length > 0)
        {
            parts.Add("Server：" + server);
        }

        string snippet = CleanSnippet(body);
        if (snippet.Length > 0)
        {
            parts.Add("响应片段：" + snippet);
        }

        return string.Join("；", parts);
    }

    /// <summary>把失败响应体清洗成单行短文本（去脚本、标签与多余空白）。</summary>
    private static string CleanSnippet(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        string text = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return SiteCollectHtmlUtil.Truncate(text, DiagnosticMaxLength);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using MemoryStream ms = new MemoryStream();
        byte[] buffer = new byte[81920];
        int total = 0;
        while (total < maxBytes)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            ms.Write(buffer, 0, read);
            total += read;
        }
        return ms.ToArray();
    }

    /// <summary>编码识别：响应头 charset → 页面 meta charset → UTF-8；iso-8859-1 按 gbk 处理。</summary>
    private static Encoding DetectEncoding(byte[] bytes, string? charsetFromHeader)
    {
        Encoding? byHeader = TryGetEncoding(charsetFromHeader);
        if (byHeader != null)
        {
            return byHeader;
        }

        int headLength = Math.Min(bytes.Length, 4096);
        if (headLength > 0)
        {
            string head = Encoding.Latin1.GetString(bytes, 0, headLength);
            Match match = HeaderCharsetPattern.Match(head);
            if (match.Success)
            {
                Encoding? byMeta = TryGetEncoding(match.Groups["c"].Value);
                if (byMeta != null)
                {
                    return byMeta;
                }
            }
        }
        return new UTF8Encoding(false);
    }

    private static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string value = name.Trim().Trim('"', '\'').ToLowerInvariant();
        if (value.Length == 0 || value == "utf-8" || value == "utf8")
        {
            return value.Length == 0 ? null : new UTF8Encoding(false);
        }
        if (value == "iso-8859-1" || value == "gb2312" || value == "gb-2312")
        {
            value = "gbk";
        }
        try
        {
            return Encoding.GetEncoding(value);
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeSafely(byte[] bytes, Encoding encoding)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
