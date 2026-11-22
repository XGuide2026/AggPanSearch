using System.Text.Json;
using AggPanSearch.Detection;
using AggPanSearch.Models;

namespace AggPanSearch.Sources.Plugins;

/// <summary>
/// 通用 JSON 网盘搜索插件：封装一类"GET 搜索接口 + 返回 JSON 条目数组"的第三方网盘搜索引擎。
///
/// 请求格式：{BaseUrl}?q={keyword}（可自定义 queryParam 名）
/// 响应格式（输入项解析规则由你来定的 delegate 提供）：
///   - 直接是链接字符串数组，如 ["https://pan.quark.cn/s/xxx", ...]
///   - 或对象数组，每个对象含 url/password/title/text 字段。
///
/// 示例（用某个已存在的第三方搜索站，域名为占位，请替换为自己可用的地址）：
///   var p = new GenericJsonNetdiskPlugin("mydisk", "https://api.example.com/search", 1);
/// </summary>
public sealed class GenericJsonNetdiskPlugin : HttpJsonPlugin
{
    /// <summary>接口返回 JSON 的 JsonElement 数组，可能结构各不相同。
    /// 为保持简洁，本插件解析两种常见结构：
    /// 1) 顶层是数组：元素为 string（链接）或 object（含 url/text/password）。
    /// 2) 顶层对象：含 data / list / results / items 等数组字段。</summary>
    private static readonly string[] ArrayKeys = { "data", "list", "results", "items", "rows" };

    private readonly string _baseUrl;
    private readonly string _queryParam;
    private readonly string _sourceName;

    public override string Name => _sourceName;
    public override int Level { get; }

    public GenericJsonNetdiskPlugin(string name, string baseUrl, int level = 2, string queryParam = "q",
        string? proxy = null, double timeoutSeconds = 15)
        : base(proxy, timeoutSeconds)
    {
        _sourceName = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _queryParam = queryParam;
        Level = level;
    }

    protected override string BuildUrl(string keyword, IDictionary<string, object>? ext)
        => $"{_baseUrl}?{_queryParam}={Uri.EscapeDataString(keyword)}";

    protected override List<SearchResult> ParseResponse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return new List<SearchResult>(); }

        List<JsonElement> items = new();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray()) items.Add(e);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // 查找数组字段
            foreach (var key in ArrayKeys)
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray()) items.Add(e);
                    break;
                }
            }
        }

        var results = new List<SearchResult>();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var entry = item.GetString();
                if (!string.IsNullOrEmpty(entry))
                {
                    var r = BuildResultFromEntry(entry, _sourceName);
                    if (r.Links.Count > 0) results.Add(r);
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var r = ParseObjectItem(item);
                if (r is not null && r.Links.Count > 0) results.Add(r);
            }
        }
        return results;
    }

    private SearchResult? ParseObjectItem(JsonElement obj)
    {
        var url = ReadString(obj, "url", "shareUrl", "link", "href", "share_link");
        var password = ReadString(obj, "password", "pwd", "extract_code", "提取码");
        var text = ReadString(obj, "text", "title", "name", "note", "content", "description", "简介");

        if (string.IsNullOrEmpty(url))
            return null;

        var norm = NetDiskDetector.NormalizeUrl(url);
        if (string.IsNullOrEmpty(norm))
            return null;

        var type = NetDiskDetector.GetLinkType(norm);
        var link = new Link
        {
            Type = type,
            Url = norm,
            Password = string.IsNullOrEmpty(password) ? null : password,
        };

        var title = text ?? norm;
        return new SearchResult
        {
            Title = title.Length > 120 ? title[..120] : title,
            Content = text ?? string.Empty,
            Channel = $"plugin:{_sourceName}",
            Datetime = DateTime.UtcNow,
            Links = new List<Link> { link },
            UniqueId = $"plugin:{_sourceName}:{norm}",
        };
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var prop) &&
                prop.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                !string.IsNullOrEmpty(prop.GetString() ?? prop.GetRawText()))
            {
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
            }
        }
        return null;
    }
}