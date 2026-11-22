namespace AggPanSearch.AI;

/// <summary>一次对话的结果，正文已去除首尾空白（未做业务清洗）。</summary>
public sealed class AiChatResponse
{
    public string Content { get; init; } = string.Empty;

    /// <summary>实际服务的模型名（厂商可能返回带日期的具体版本）。</summary>
    public string Model { get; init; } = string.Empty;

    public AiUsage Usage { get; init; } = new AiUsage();

    /// <summary>本次调用耗时（毫秒），含重试等待。</summary>
    public long ElapsedMs { get; init; }

    public string ProviderKey { get; init; } = string.Empty;
}
