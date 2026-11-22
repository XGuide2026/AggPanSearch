namespace AggPanSearch.AI;

/// <summary>
/// DeepSeek 接入项。因为使用 OpenAI 兼容协议，这里只有一条描述符，
/// 不需要单独实现客户端；换成 Moonshot、智谱等同样只加一条描述。
/// </summary>
internal static class DeepSeekProvider
{
    public const string Key = "deepseek";

    public static void Register()
    {
        AiProviderRegistry.Register(
            new AiProviderDescriptor(
                Key,
                "DeepSeek",
                AiProtocol.OpenAiCompatible,
                "https://api.deepseek.com",
                "deepseek-flash",
                "ai_deepseek_apikey"),
            options => new OpenAiCompatibleChatClient(options));
    }
}
