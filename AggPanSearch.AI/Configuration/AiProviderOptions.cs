namespace AggPanSearch.AI;

/// <summary>
/// AI 调用参数。全部由调用方（业务层从配置表读取）注入，
/// 类库内部不访问数据库、不读 appsettings，保证可独立测试。
/// </summary>
public sealed record AiProviderOptions
{
    /// <summary>厂商标识，留空表示使用默认厂商（deepseek）。</summary>
    public string ProviderKey { get; init; } = string.Empty;

    /// <summary>接口根地址，留空使用厂商默认值。可带或不带 /v1。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>API Key，留空会在创建客户端时直接报配置错误。</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>模型名，留空使用厂商默认值。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>采样温度。</summary>
    public double Temperature { get; init; } = 0.5;

    /// <summary>最大生成 token 数。</summary>
    public int MaxTokens { get; init; } = 600;

    /// <summary>单次请求超时（秒），不含重试等待。</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>User-Agent，便于厂商侧识别调用来源。</summary>
    public string UserAgent { get; init; } = "AggPanSearch.AI/1.0";
}
