using System.Text.Json;
using AggPanSearch.Detection;
using AggPanSearch.Models;
using AggPanSearch.Util;

namespace AggPanSearch.Sources.Plugins;

/// <summary>
/// 可配置的 HTTP JSON 搜索插件骨架。
/// 许多第三方网盘搜索源采用"GET/POST 请求 + 返回 JSON 列表"的格式。
/// 通过子类覆写 GetUrl / ParseResponse 即可接入一个新的搜索源（对应 Go 项目每个 plugin 子目录）。
/// </summary>
public abstract class HttpJsonPlugin : IPlugin
{
    public abstract string Name { get; }
    public abstract int Level { get; }

    /// <summary>代理地址（可为 null）。</summary>
    protected string? Proxy { get; }

    private readonly HttpUtil _http;

    protected HttpJsonPlugin(string? proxy, double timeoutSeconds = 15)
    {
        Proxy = proxy;
        _http = new HttpUtil(proxy, timeoutSeconds);
    }

    /// <summary>构造请求 URL（GET 搜索通常把关键词填入查询串）。</summary>
    protected abstract string BuildUrl(string keyword, IDictionary<string, object>? ext);

    /// <summary>解析响应为原始文本/对象。</summary>
    protected abstract List<SearchResult> ParseResponse(string json);

    public async Task<PluginSearchResult> SearchAsync(string keyword, IDictionary<string, object>? ext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildUrl(keyword, ext);
            var body = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            if (body is null)
                return Empty("请求失败");

            var results = ParseResponse(body);
            return new PluginSearchResult
            {
                Source = $"plugin:{Name}",
                IsFinal = true,
                Timestamp = DateTime.UtcNow,
                Results = results,
            };
        }
        catch (Exception ex)
        {
            return new PluginSearchResult
            {
                Source = $"plugin:{Name}",
                IsFinal = false,
                Timestamp = DateTime.UtcNow,
                Message = ex.Message,
            };
        }
    }

    private static PluginSearchResult Empty(string message) => new()
    {
        IsFinal = false,
        Timestamp = DateTime.UtcNow,
        Message = message,
    };

    /// <summary>
    /// 常用解析助手：把第三方返回的链接条目转为 Link（自动识别网盘类型并抓提取码）。
    /// </summary>
    protected static SearchResult BuildResultFromEntry(string entryText, string sourceTag)
    {
        var candidates = NetDiskDetector.ExtractNetDiskLinks(entryText);
        var links = candidates.Select(c => new Link
        {
            Type = c.Type,
            Url = c.Url,
            Password = string.IsNullOrEmpty(NetDiskDetector.ExtractPassword(entryText, c.Url)) ? null : NetDiskDetector.ExtractPassword(entryText, c.Url),
        }).ToList();

        return new SearchResult
        {
            Channel = $"plugin:{sourceTag}",
            Title = BuildTitle(entryText),
            Content = entryText,
            Links = links,
            Datetime = DateTime.UtcNow,
        };
    }

    private static string BuildTitle(string text)
    {
        // 取第一行作为标题
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? text.Length > 60 ? text[..60] : text : line.Trim();
    }

    protected static T? SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    /// <summary>释放资源。</summary>
    public void Dispose() => _http.Dispose();
}