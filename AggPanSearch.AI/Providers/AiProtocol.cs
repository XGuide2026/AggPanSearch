namespace AggPanSearch.AI;

/// <summary>
/// 厂商使用的协议族。OpenAI 兼容协议的可直接复用
/// OpenAiCompatibleChatClient，只需换地址、模型与 Key。
/// </summary>
public enum AiProtocol
{
    /// <summary>OpenAI chat/completions 兼容（DeepSeek、Moonshot、智谱、硅基流动等）。</summary>
    OpenAiCompatible = 0,

    /// <summary>Anthropic Messages 协议。</summary>
    Anthropic = 1,

    /// <summary>Google Gemini 协议。</summary>
    Gemini = 2
}
