namespace AggPanSearch.AI;

/// <summary>
/// 一次对话请求。Temperature / MaxTokens 为空时使用
/// <see cref="AiProviderOptions"/> 中的默认值，便于单个调用点做微调而不改全局配置。
/// </summary>
public sealed class AiChatRequest
{
    public AiChatRequest(IReadOnlyList<AiMessage> messages)
    {
        Messages = messages ?? Array.Empty<AiMessage>();
    }

    public IReadOnlyList<AiMessage> Messages { get; }

    /// <summary>采样温度，null 表示使用全局默认值。</summary>
    public double? Temperature { get; init; }

    /// <summary>最大生成 token 数，null 表示使用全局默认值。</summary>
    public int? MaxTokens { get; init; }

    /// <summary>便捷构造：system + user 两条消息。</summary>
    public static AiChatRequest FromPrompt(string system, string user)
    {
        return new AiChatRequest(new[]
        {
            AiMessage.System(system),
            AiMessage.User(user)
        });
    }
}
