namespace AggPanSearch.AI;

/// <summary>
/// 一条对话消息，与 OpenAI chat/completions 的 messages 元素一一对应。
/// 角色统一用字符串，避免为不同厂商维护多套角色枚举。
/// </summary>
public sealed class AiMessage
{
    public AiMessage(string role, string content)
    {
        Role = role ?? string.Empty;
        Content = content ?? string.Empty;
    }

    public string Role { get; }

    public string Content { get; }

    public static AiMessage System(string content)
    {
        return new AiMessage("system", content);
    }

    public static AiMessage User(string content)
    {
        return new AiMessage("user", content);
    }

    public static AiMessage Assistant(string content)
    {
        return new AiMessage("assistant", content);
    }
}
