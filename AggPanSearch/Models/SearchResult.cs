using System.Text.Json.Serialization;

namespace AggPanSearch.Models;

/// <summary>
/// 网盘链接。对应 Go 项目的 model.Link。
/// </summary>
public sealed class Link
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("datetime")]
    public DateTime? Datetime { get; set; }

    /// <summary>作品标题（用于区分同一消息中多个作品的链接）。</summary>
    [JsonPropertyName("work_title")]
    public string? WorkTitle { get; set; }
}

/// <summary>
/// 单条搜索结果。对应 Go 项目的 model.SearchResult。
/// </summary>
public sealed class SearchResult
{
    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("unique_id")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public DateTime Datetime { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("links")]
    public List<Link> Links { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    /// <summary>生成的一段唯一标识（在返回前惰性赋值）。</summary>
    [JsonIgnore]
    public string ComputedUniqueId { get; set; } = string.Empty;
}

/// <summary>
/// 合并后的网盘链接。对应 Go 项目的 model.MergedLink。
/// </summary>
public sealed class MergedLink
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public DateTime? Datetime { get; set; }

    /// <summary>数据来源：tg:频道名 或 plugin:插件名。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }
}

/// <summary>
/// 按网盘类型分组的合并链接。对应 Go 项目的 model.MergedLinks。
/// </summary>
public sealed class MergedLinks : Dictionary<string, List<MergedLink>>
{
}

/// <summary>
/// 搜索响应。对应 Go 项目的 model.SearchResponse。
/// </summary>
public sealed class SearchResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("results")]
    public List<SearchResult>? Results { get; set; }

    [JsonPropertyName("merged_by_type")]
    public MergedLinks? MergedByType { get; set; }

    /// <summary>
    /// 本次结果是否直接来自缓存（命中缓存不发起真实搜索）。
    /// 仅本地统计使用，不参与 JSON 序列化，保持与 Go 版接口输出一致。
    /// </summary>
    [JsonIgnore]
    public bool FromCache { get; set; }
}

/// <summary>
/// 插件搜索结果。对应 Go 项目的 model.PluginSearchResult。
/// </summary>
public sealed class PluginSearchResult
{
    [JsonPropertyName("results")]
    public List<SearchResult> Results { get; set; } = new();

    /// <summary>是否为最终完整结果（非流式/增量结果）。</summary>
    [JsonPropertyName("is_final")]
    public bool IsFinal { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public bool IsEmpty() => Results is null || Results.Count == 0;
    public int Count() => Results?.Count ?? 0;
    public List<SearchResult> GetResults() => Results ?? new();
}