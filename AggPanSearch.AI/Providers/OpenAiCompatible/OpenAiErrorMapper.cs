using System.Text.Json;

namespace AggPanSearch.AI;

/// <summary>
/// 把 OpenAI 兼容协议的错误响应翻译成可展示的中文异常。
/// 各厂商错误体结构略有差异（error.message / message / 纯文本），这里做兼容解析。
/// </summary>
internal sealed class OpenAiErrorMapper
{
    public AiException Map(int statusCode, string responseBody, string providerKey, string model)
    {
        string upstream = ExtractMessage(responseBody);

        switch (statusCode)
        {
            case 401:
            case 403:
                return new AiException(
                    AiErrorKind.Unauthorized,
                    "AI 接口 Key 无效或已失效，请到「AI设置」页面重新填写",
                    statusCode,
                    providerKey);

            case 402:
                return new AiException(
                    AiErrorKind.InsufficientBalance,
                    "AI 账户余额不足，请充值后再试",
                    statusCode,
                    providerKey);

            case 404:
                return new AiException(
                    AiErrorKind.BadRequest,
                    "AI 接口地址不正确（HTTP 404），请到「AI设置」页面检查接口地址",
                    statusCode,
                    providerKey);

            case 429:
                return new AiException(
                    AiErrorKind.RateLimited,
                    "AI 接口请求过于频繁（限流），请稍后再试",
                    statusCode,
                    providerKey);

            case 400:
                if (IsModelProblem(upstream))
                {
                    return new AiException(
                        AiErrorKind.ModelNotFound,
                        $"AI 模型「{model}」不存在或已下线，请到「AI设置」页面修改 AI 模型（可在接口地址后拼接 /models 查询可用模型）",
                        statusCode,
                        providerKey);
                }
                return new AiException(
                    AiErrorKind.BadRequest,
                    "AI 接口拒绝了本次请求：" + upstream,
                    statusCode,
                    providerKey);

            default:
                if (statusCode >= 500)
                {
                    return new AiException(
                        AiErrorKind.UpstreamError,
                        $"AI 服务端异常（HTTP {statusCode}），请稍后重试",
                        statusCode,
                        providerKey);
                }
                return new AiException(
                    AiErrorKind.Unknown,
                    $"AI 接口调用失败（HTTP {statusCode}）：{upstream}",
                    statusCode,
                    providerKey);
        }
    }

    /// <summary>从错误体中尽量取出可读原因，取不到就用截断后的原文。</summary>
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "无响应内容";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        string? text = error.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                    else if (error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out JsonElement errorMessage)
                        && errorMessage.ValueKind == JsonValueKind.String)
                    {
                        string? text = errorMessage.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                }

                if (root.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    string? text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应，走下面的原文截断
        }

        string raw = body.Trim();
        if (raw.Length <= 200)
        {
            return raw;
        }
        return raw.Substring(0, 200) + "...";
    }

    private static bool IsModelProblem(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string text = message.ToLowerInvariant();
        return text.Contains("model not exist")
            || text.Contains("model_not_found")
            || text.Contains("model does not exist")
            || text.Contains("invalid model")
            || text.Contains("unknown model")
            || text.Contains("model is not supported");
    }
}
