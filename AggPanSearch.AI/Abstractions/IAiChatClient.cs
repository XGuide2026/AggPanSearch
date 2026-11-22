namespace AggPanSearch.AI;

/// <summary>
/// 聊天补全客户端。新增厂商只需提供一个实现并在 Provider 注册表登记，
/// 业务层代码无需改动。失败统一抛 <see cref="AiException"/>。
/// </summary>
public interface IAiChatClient
{
    /// <summary>厂商标识，例如 deepseek。</summary>
    string ProviderKey { get; }

    /// <summary>当前使用的模型名。</summary>
    string Model { get; }

    /// <summary>发起一次非流式对话。</summary>
    Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken token = default);

    /// <summary>便捷重载：system + user 两条消息。</summary>
    Task<AiChatResponse> ChatAsync(string system, string user, int? maxTokens = null, CancellationToken token = default);
}
