namespace AggPanSearch.AI;

/// <summary>
/// 厂商描述符：把「地址、默认模型、Key 的配置键名」收敛到一处。
/// 业务层不出现任何厂商字面量，全部从这里取；新增厂商只加一条描述。
/// </summary>
/// <param name="Key">厂商标识，写入配置项 ai_provider。</param>
/// <param name="DisplayName">展示名称，用于提示文案。</param>
/// <param name="Protocol">协议族。</param>
/// <param name="DefaultBaseUrl">默认接口根地址。</param>
/// <param name="DefaultModel">默认模型名。</param>
/// <param name="ApiKeyConfKey">该厂商 API Key 所在的配置键名，约定为 ai_{provider}_apikey。</param>
public sealed record AiProviderDescriptor(
    string Key,
    string DisplayName,
    AiProtocol Protocol,
    string DefaultBaseUrl,
    string DefaultModel,
    string ApiKeyConfKey);
