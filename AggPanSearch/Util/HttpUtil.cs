using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AggPanSearch.Util;

/// <summary>
/// 一次 HTTP GET 的完整结果：状态码为 0 表示请求没走到响应（网络异常/超时），Error 为具体原因。
/// </summary>
public sealed class HttpFetchResult
{
    public int StatusCode { get; init; }

    public string? Content { get; init; }

    public string? Error { get; init; }

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
}

/// <summary>
/// 轻量 HTTP 请求封装，支持 HTTP/SOCKS5 代理、超时、UA 伪装与重试。
/// 对应 Go 项目 util/http_util.go 的请求封装能力。
/// </summary>
public sealed class HttpUtil : IDisposable
{
    private readonly HttpClient _client;

    /// <summary>
    /// 创建 HttpUtil。
    /// </summary>
    /// <param name="proxy">代理地址（socks5://host:port 或 http://host:port），可为 null。</param>
    /// <param name="timeoutSeconds">单次请求超时（秒）。</param>
    /// <param name="maxConnections">最大并发连接数。</param>
    public HttpUtil(string? proxy, double timeoutSeconds = 30, int maxConnections = 0)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = maxConnections > 0 ? maxConnections : Environment.ProcessorCount * 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                var uri = new Uri(proxy);
                handler.Proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
                handler.UseProxy = true;
                // .NET (10) SocketsHttpHandler 原生支持 http/socks4/socks5 代理 scheme，
                // 无需手动实现 SOCKS5 握手。
            }
            catch (UriFormatException)
            {
                // 忽略无效代理，回退为直连
            }
        }

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
    }

    /// <summary>
    /// 发起 GET 请求，返回文本内容。
    /// maxBytes > 0 时流式限长读取（避免大响应体产生 LOH 大对象引发 GC 长停顿），超限部分直接丢弃。
    /// </summary>
    public async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken = default, int maxBytes = 0)
    {
        // 取消兜底：部分阶段（DNS/连接/TLS/等待响应头/读取大响应体）对 CancellationToken 响应迟钝，
        // 若窗口取消 token 已触发，用 WaitAsync 强制秒级中断异步等待，避免在途请求长期占用
        // 线程池线程/CPU，拖慢主线程的归并/排序等同步段（详见 WorkerPool 最快响应窗口机制）。
        using var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        if (maxBytes > 0)
            return await ReadStringLimitedAsync(resp.Content, maxBytes, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式读取响应体，最多读取 maxBytes 字节即停止（t.me 搜索页为 UTF-8，直接按 UTF-8 解码）。
    /// </summary>
    private static async Task<string> ReadStringLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(Math.Min(maxBytes, 81920));
        var buffer = new byte[81920];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// 发起 GET 请求，返回「状态码 + 内容 + 错误信息」的完整结果，便于调用方区分
    /// 404/被限流(429)/网络异常/超时 等失败原因（GetStringAsync 只能拿到 null）。
    /// </summary>
    public async Task<HttpFetchResult> GetStringResultAsync(string url, CancellationToken cancellationToken = default,
        int maxBytes = 0)
    {
        try
        {
            using var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            int statusCode = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                return new HttpFetchResult { StatusCode = statusCode };
            }
            string content = maxBytes > 0
                ? await ReadStringLimitedAsync(resp.Content, maxBytes, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false)
                : await resp.Content.ReadAsStringAsync(cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpFetchResult { StatusCode = statusCode, Content = content };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout 到期也会抛 OperationCanceledException（不是调用方取消）
            return new HttpFetchResult { StatusCode = 0, Error = "请求超时" };
        }
        catch (HttpRequestException ex)
        {
            return new HttpFetchResult { StatusCode = 0, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new HttpFetchResult { StatusCode = 0, Error = ex.Message };
        }
    }

    /// <summary>
    /// 轻量可达性探测：仅读取响应头即返回，不下载响应体（避免 t.me 首页大 HTML 拖慢探测）。
    /// </summary>
    public async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        try
        {
            using var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 发起 GET 请求，返回字节内容。
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发起 POST 请求（JSON 或表单），返回文本内容。
    /// </summary>
    public async Task<string?> PostAsync(string url, string? jsonBody = null, string? contentType = "application/json",
        IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation("Authorization", v);
                else
                    request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}