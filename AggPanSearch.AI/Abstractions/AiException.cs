namespace AggPanSearch.AI;

/// <summary>
/// AI 调用异常。<see cref="Exception.Message"/> 一律是可直接展示给用户的中文文案，
/// 且不包含 API Key、请求头等敏感内容。
/// </summary>
public sealed class AiException : Exception
{
    public AiException(
        AiErrorKind kind,
        string message,
        int? statusCode = null,
        string providerKey = "",
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ProviderKey = providerKey ?? string.Empty;
    }

    /// <summary>失败分类。</summary>
    public AiErrorKind Kind { get; }

    /// <summary>上游 HTTP 状态码，未发出请求时为空。</summary>
    public int? StatusCode { get; }

    /// <summary>厂商标识，便于日志定位。</summary>
    public string ProviderKey { get; }

    /// <summary>是否属于「稍后重试可能成功」的失败。</summary>
    public bool IsTransient
    {
        get
        {
            return Kind == AiErrorKind.RateLimited
                || Kind == AiErrorKind.UpstreamError
                || Kind == AiErrorKind.Timeout
                || Kind == AiErrorKind.Network;
        }
    }
}
