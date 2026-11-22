using AggPanSearch.Models;

namespace AggPanSearch.Sources;

/// <summary>
/// 网盘搜索插件抽象。对应 Go 项目的 plugin 接口。
/// 每种第三方搜索源实现一个插件，通过 <see cref="IPlugin.SearchAsync"/> 返回结果。
/// </summary>
public interface IPlugin
{
    /// <summary>插件唯一标识名（如 "jikepan"、"pansearch"）。</summary>
    string Name { get; }

    /// <summary>插件等级（1=最可信优质，4=较差），用于排序权重。</summary>
    int Level { get; }

    /// <summary>
    /// 执行搜索。
    /// </summary>
    /// <param name="keyword">关键词。</param>
    /// <param name="ext">透传的扩展参数。</param>
    Task<PluginSearchResult> SearchAsync(string keyword, IDictionary<string, object>? ext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件仓库：按名称注册、查找插件。
/// </summary>
public sealed class PluginRegistry
{
    /// <summary>全局默认插件仓库。</summary>
    public static PluginRegistry Instance { get; } = new();

    private readonly Dictionary<string, IPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IPlugin plugin) => _plugins[plugin.Name] = plugin;

    /// <summary>批量注册。</summary>
    public void RegisterRange(IEnumerable<IPlugin> plugins)
    {
        foreach (var p in plugins) Register(p);
    }

    public bool TryGet(string name, out IPlugin? plugin) => _plugins.TryGetValue(name, out plugin);

    public IEnumerable<IPlugin> All => _plugins.Values;

    public int Count => _plugins.Count;

    /// <summary>按名称过滤返回插件（空集合返回全部）。</summary>
    public IReadOnlyList<IPlugin> Filter(string[]? names)
    {
        if (names is null || names.Length == 0)
            return _plugins.Values.ToList();

        var result = new List<IPlugin>();
        foreach (var name in names)
        {
            if (_plugins.TryGetValue(name, out var p))
                result.Add(p);
        }
        return result;
    }
}