using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AggPanSearch.AI;

/// <summary>
/// OpenAI chat/completions 兼容协议的通用实现。
/// DeepSeek、Moonshot、智谱、硅基流动等厂商可直接复用，只需在描述符里配置地址与模型。
/// </summary>
internal sealed class OpenAiCompatibleChatClient : IAiChatClient, IAiModelCatalog
{
    private const int MaxRetryTimes = 2;

    private static readonly HttpClient SharedClient = CreateHttpClient();

    private readonly AiProviderOptions options;
    private readonly OpenAiErrorMapper errorMapper = new OpenAiErrorMapper();
    private readonly string chatUrl;
    private readonly string modelsUrl;

    public OpenAiCompatibleChatClient(AiProviderOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        chatUrl = BuildUrl(options.BaseUrl, "chat/completions");
        modelsUrl = BuildUrl(options.BaseUrl, "models");
    }

    public string ProviderKey
    {
        get { return options.ProviderKey; }
    }

    public string Model
    {
        get { return options.Model; }
    }

    public Task<AiChatResponse> ChatAsync(string system, string user, int? maxTokens = null, CancellationToken token = default)
    {
        AiChatRequest request = new AiChatRequest(new[] { AiMessage.System(system), AiMessage.User(user) })
        {
            MaxTokens = maxTokens
        };
        return ChatAsync(request, token);
    }

    public async Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken token = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.Messages.Count == 0)
        {
            throw new AiException(AiErrorKind.BadRequest, "对话消息为空，无法调用 AI 接口", null, ProviderKey);
        }

        int maxTokens = request.MaxTokens ?? options.MaxTokens;
        double temperature = request.Temperature ?? options.Temperature;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = request.Messages
                .Select(t => new Dictionary<string, object?> { ["role"] = t.Role, ["content"] = t.Content })
                .ToArray(),
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false
        };

        string body = JsonSerializer.Serialize(payload);
        Stopwatch watch = Stopwatch.StartNew();
        string responseText = await SendAsync(HttpMethod.Post, chatUrl, body, token).ConfigureAwait(false);
        watch.Stop();

        return ParseChatResponse(responseText, watch.ElapsedMilliseconds);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
    {
        string responseText = await SendAsync(HttpMethod.Get, modelsUrl, null, token).ConfigureAwait(false);
        List<string> models = new List<string>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out JsonElement id)
                        && id.ValueKind == JsonValueKind.String)
                    {
                        string? value = id.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            models.Add(value.Trim());
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.UpstreamError, "AI 模型列表返回格式无法解析", 200, ProviderKey, ex);
        }

        if (models.Count == 0)
        {
            throw new AiException(AiErrorKind.UpstreamError, "AI 接口未返回任何可用模型", 200, ProviderKey);
        }

        return models;
    }

    private AiChatResponse ParseChatResponse(string responseText, long elapsedMs)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            JsonElement root = document.RootElement;

            string content = string.Empty;
            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement contentElement)
                    && contentElement.ValueKind == JsonValueKind.String)
                {
                    content = contentElement.GetString() ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AiException(AiErrorKind.UpstreamError, "AI 返回内容为空，请重新生成", 200, ProviderKey);
            }

            AiUsage usage = new AiUsage();
            if (root.TryGetProperty("usage", out JsonElement usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = new AiUsage
                {
                    PromptTokens = ReadInt(usageElement, "prompt_tokens"),
                    CompletionTokens = ReadInt(usageElement, "completion_tokens"),
                    TotalTokens = ReadInt(usageElement, "total_tokens")
                };
            }

            string model = options.Model;
            if (root.TryGetProperty("model", out JsonElement modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString() ?? options.Model;
            }

            return new AiChatResponse
            {
                Content = content.Trim(),
                Model = model,
                Usage = usage,
                ElapsedMs = elapsedMs,
                ProviderKey = ProviderKey
            };
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.UpstreamError, "AI 接口返回了无法解析的内容，请稍后重试", 200, ProviderKey, ex);
        }
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result))
        {
            return result;
        }
        return 0;
    }

    /// <summary>发送请求，429/5xx/超时/网络异常时按 1s、2s 退避重试。</summary>
    private async Task<string> SendAsync(HttpMethod method, string url, string? body, CancellationToken token)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            using HttpRequestMessage request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.ApiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(options.UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            }
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));

            HttpResponseMessage? response = null;
            try
            {
                response = await SharedClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                int statusCode = (int)response.StatusCode;
                string responseText = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return responseText;
                }

                bool retryable = statusCode == 429 || statusCode >= 500;
                if (retryable && attempt <= MaxRetryTimes)
                {
                    await Task.Delay(RetryDelay(attempt), token).ConfigureAwait(false);
                    continue;
                }

                throw errorMapper.Map(statusCode, responseText, ProviderKey, options.Model);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (attempt <= MaxRetryTimes)
                {
                    await Task.Delay(RetryDelay(attempt), token).ConfigureAwait(false);
                    continue;
                }
                throw new AiException(
                    AiErrorKind.Timeout,
                    $"AI 接口超时（超过 {options.TimeoutSeconds} 秒未响应），请稍后重试",
                    null,
                    ProviderKey);
            }
            catch (HttpRequestException ex)
            {
                if (attempt <= MaxRetryTimes)
                {
                    await Task.Delay(RetryDelay(attempt), token).ConfigureAwait(false);
                    continue;
                }
                throw new AiException(
                    AiErrorKind.Network,
                    "无法连接 AI 接口（网络不可达或代理异常）：" + ex.Message,
                    null,
                    ProviderKey,
                    ex);
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                }
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Pow(2, Math.Max(0, attempt - 1)));
    }

    /// <summary>
    /// 规范化接口地址：去掉尾部斜杠，并容错用户误填的完整接口路径。
    /// 保留 /v1 前缀（DeepSeek 两种写法都接受）。
    /// </summary>
    private static string BuildUrl(string baseUrl, string path)
    {
        string root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (root.Length == 0)
        {
            root = "https://api.deepseek.com";
        }

        string[] suffixes = { "/chat/completions", "/models" };
        foreach (string suffix in suffixes)
        {
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                root = root.Substring(0, root.Length - suffix.Length).TrimEnd('/');
                break;
            }
        }

        return root + "/" + path;
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All
        };

        return new HttpClient(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }
}
