using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AggPanSearch.Detection;

/// <summary>
/// 正则缓存：每个正则仅编译一次，后续复用。
/// 对应 Go 项目 regex_util.go 中"提取到包级只编译一次"的优化。
/// 注意：此前实现每次调用都 new Regex（Compiled 模式重新 JIT 编译），
/// 在 TG 解析等逐消息块调用场景下会反复编译，单页可累计数秒，拖垮线程池。
/// </summary>
internal static class RegexCache
{
    private const RegexOptions Baseline = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static Regex Get(string pattern)
        => Cache.GetOrAdd(pattern, p => new Regex(p, Baseline, TimeSpan.FromSeconds(2)));

    /// <summary>创建一个忽略大小写的正则（用于 URL 域名匹配等）。</summary>
    public static Regex GetIgnoreCase(string pattern)
        => Get(pattern, RegexOptions.IgnoreCase);

    /// <summary>创建带自定义选项的正则（如 RegexOptions.Singleline）。</summary>
    public static Regex Get(string pattern, RegexOptions extraOptions)
    {
        var key = $"{(int)extraOptions}|{pattern}";
        return Cache.GetOrAdd(key, _ => new Regex(pattern, Baseline | extraOptions, TimeSpan.FromSeconds(2)));
    }

    /// <summary>识别正则缓存基选项（供内部检查）。</summary>
    internal static Regex GetWithBaseline(string pattern, RegexOptions extraOptions)
        => Get(pattern, extraOptions);
}
