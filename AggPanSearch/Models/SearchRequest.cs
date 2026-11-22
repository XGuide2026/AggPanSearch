using System.Text.Json.Serialization;

namespace AggPanSearch.Models;

/// <summary>
/// 过滤配置。Include 为 OR 关系（命中任一即包含），Exclude 为 AND 关系（命中任一即排除）。
/// 对应 Go 项目的 model.FilterConfig。
/// </summary>
public sealed class SearchFilter
{
    /// <summary>包含关键词列表（OR 关系，命中任一则保留）。可选。</summary>
    [JsonPropertyName("include")]
    public IReadOnlyList<string>? Include { get; set; }

    /// <summary>排除关键词列表（命中任一则排除）。可选。</summary>
    [JsonPropertyName("exclude")]
    public IReadOnlyList<string>? Exclude { get; set; }
}

/// <summary>
/// 搜索请求参数。对应 Go 项目的 model.SearchRequest。
/// </summary>
public sealed class SearchRequest
{
    /// <summary>搜索关键词（必填）。</summary>
    [JsonPropertyName("kw")]
    public string Keyword { get; set; } = string.Empty;

    /// <summary>搜索的频道列表。为空则使用配置默认频道。</summary>
    [JsonPropertyName("channels")]
    public IReadOnlyList<string>? Channels { get; set; }

    /// <summary>并发搜索数量，0 表示使用配置值。</summary>
    [JsonPropertyName("conc")]
    public int Concurrency { get; set; }

    /// <summary>强制刷新，跳过缓存。</summary>
    [JsonPropertyName("refresh")]
    public bool ForceRefresh { get; set; }

    /// <summary>结果类型：all / results / merge。</summary>
    [JsonPropertyName("res")]
    public string? ResultType { get; set; }

    /// <summary>数据来源类型：all / tg / plugin。</summary>
    [JsonPropertyName("src")]
    public string? SourceType { get; set; }

    /// <summary>指定搜索的插件列表。为空则搜索全部启用插件。</summary>
    [JsonPropertyName("plugins")]
    public IReadOnlyList<string>? Plugins { get; set; }

    /// <summary>扩展参数，透传插件。</summary>
    [JsonPropertyName("ext")]
    public IDictionary<string, object>? Ext { get; set; }

    /// <summary>指定返回的网盘类型列表，为空返回全部。</summary>
    [JsonPropertyName("cloud_types")]
    public IReadOnlyList<string>? CloudTypes { get; set; }

    /// <summary>过滤配置。</summary>
    [JsonPropertyName("filter")]
    public SearchFilter? Filter { get; set; }

    /// <summary>规范化结果类型（小写，默认 merge）。</summary>
    public string NormalizedResultType()
        => (ResultType ?? string.Empty).ToLowerInvariant() switch
        {
            "all" => "all",
            "results" => "results",
            _ => "merge",
        };

    /// <summary>规范化来源类型（小写，默认 all）。</summary>
    public string NormalizedSourceType()
        => (SourceType ?? string.Empty).ToLowerInvariant() switch
        {
            "tg" => "tg",
            "plugin" => "plugin",
            _ => "all",
        };
}

/// <summary>
/// 请求结果类型常量。
/// </summary>
public static class ResultTypes
{
    public const string All = "all";
    public const string Results = "results";
    public const string Merge = "merge";
}

/// <summary>
/// 数据来源类型常量。
/// </summary>
public static class SourceTypes
{
    public const string All = "all";
    public const string Tg = "tg";
    public const string Plugin = "plugin";
}