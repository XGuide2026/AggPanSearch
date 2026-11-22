namespace AggPanSearch.AI;

/// <summary>
/// 厂商注册表。默认注册 DeepSeek；接入新厂商时在启动阶段调用一次
/// <see cref="Register"/> 即可，业务层只依赖 <see cref="IAiChatClient"/>。
/// </summary>
public sealed class AiProviderRegistry
{
    /// <summary>未配置 ai_provider 时使用的厂商。</summary>
    public const string DefaultProviderKey = "deepseek";

    private static readonly object SyncRoot = new object();
    private static readonly Dictionary<string, Entry> Entries =
        new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    static AiProviderRegistry()
    {
        DeepSeekProvider.Register();
    }

    /// <summary>注册厂商。重复注册同一 Key 会以后者覆盖前者，便于测试替换。</summary>
    public static void Register(AiProviderDescriptor descriptor, Func<AiProviderOptions, IAiChatClient> factory)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }
        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }
        if (string.IsNullOrWhiteSpace(descriptor.Key))
        {
            throw new ArgumentException("厂商标识不能为空", nameof(descriptor));
        }

        lock (SyncRoot)
        {
            Entries[descriptor.Key.Trim()] = new Entry(descriptor, factory);
        }
    }

    /// <summary>已注册的厂商清单，用于后台配置页展示可选值。</summary>
    public static IReadOnlyList<AiProviderDescriptor> Descriptors
    {
        get
        {
            lock (SyncRoot)
            {
                return Entries.Values.Select(t => t.Descriptor).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>按厂商标识取描述符，标识为空时返回默认厂商。</summary>
    public static AiProviderDescriptor GetDescriptor(string providerKey)
    {
        string key = string.IsNullOrWhiteSpace(providerKey) ? DefaultProviderKey : providerKey.Trim();
        Entry? entry;
        lock (SyncRoot)
        {
            if (Entries.TryGetValue(key, out entry) && entry != null)
            {
                return entry.Descriptor;
            }
        }

        string supported = string.Join("、", Descriptors.Select(t => t.Key));
        throw new AiException(
            AiErrorKind.InvalidConfig,
            $"不支持的 AI 厂商「{key}」，请在「基础设置 - AI设置」修改 AI 厂商（当前支持：{supported}）");
    }

    /// <summary>
    /// 根据配置创建客户端，并把留空的地址、模型、Key 用描述符默认值补齐。
    /// </summary>
    public static IAiChatClient Create(AiProviderOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        AiProviderDescriptor descriptor = GetDescriptor(options.ProviderKey);
        string apiKey = (options.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length == 0)
        {
            throw new AiException(
                AiErrorKind.InvalidConfig,
                $"未配置 {descriptor.DisplayName} 的 API Key，请到「基础设置 - AI设置」填写（配置项 {descriptor.ApiKeyConfKey}）",
                null,
                descriptor.Key);
        }

        AiProviderOptions resolved = options with
        {
            ProviderKey = descriptor.Key,
            ApiKey = apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? descriptor.DefaultBaseUrl : options.BaseUrl.Trim(),
            Model = string.IsNullOrWhiteSpace(options.Model) ? descriptor.DefaultModel : options.Model.Trim()
        };

        Entry? entry;
        lock (SyncRoot)
        {
            if (!Entries.TryGetValue(descriptor.Key, out entry) || entry == null)
            {
                throw new AiException(AiErrorKind.InvalidConfig, $"厂商「{descriptor.Key}」未注册客户端工厂", null, descriptor.Key);
            }
        }

        return entry.Factory(resolved);
    }

    private sealed class Entry
    {
        public Entry(AiProviderDescriptor descriptor, Func<AiProviderOptions, IAiChatClient> factory)
        {
            Descriptor = descriptor;
            Factory = factory;
        }

        public AiProviderDescriptor Descriptor { get; }

        public Func<AiProviderOptions, IAiChatClient> Factory { get; }
    }
}
