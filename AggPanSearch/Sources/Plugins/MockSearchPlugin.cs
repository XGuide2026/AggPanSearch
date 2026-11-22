using System.Text.Json;
using AggPanSearch.Models;

namespace AggPanSearch.Sources.Plugins;

/// <summary>
/// 演示用插件：内置少量样例数据，无需网络即可验证 TG/插件搜索流水线。
/// 仅用于开发/演示，正式使用请替换为真实 HTTP 插件。
/// </summary>
public sealed class MockSearchPlugin : IPlugin
{
    public string Name => "mock";
    public int Level => 3;

    private static readonly (string Keyword, List<SearchResult> Results)[] Dataset =
    {
        ("凡人修仙传", new List<SearchResult>
        {
            New("凡人修仙传 仙逆 合集 4K", "https://pan.quark.cn/s/abcd1234", "1234",
                new[] { "腾讯", "夸克", "全4季" }, "tg:mock"),
            New("凡人修仙传电影版", "https://pan.baidu.com/s/1abCDefGhIjKL", "abcd",
                new[] { "高清", "百度" }, "tg:mock"),
        }),
        ("唐朝诡事录", new List<SearchResult>
        {
            New("唐朝诡事录之西行 全2季 全集 1080P", "https://pan.quark.cn/s/wxyz9999", "9999",
                new[] { "全2季", "全集", "1080P" }, "tg:mock"),
        }),
        ("大话西游", new List<SearchResult>
        {
            New("大话西游月光宝盒 4K修复", "https://www.alipan.com/s/aliun123", "",
                new[] { "周星驰", "经典" }, "tg:mock"),
        }),
    };

    public Task<PluginSearchResult> SearchAsync(string keyword, IDictionary<string, object>? ext,
        CancellationToken cancellationToken = default)
    {
        var found = Dataset.Where(x => keyword != null && x.Keyword.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Results)
            .ToList();

        return Task.FromResult(new PluginSearchResult
        {
            Source = $"plugin:{Name}",
            IsFinal = true,
            Timestamp = DateTime.UtcNow,
            Results = found,
            Message = found.Count > 0 ? "ok" : "无结果",
        });
    }

    private static SearchResult New(string title, string url, string password, string[] tags, string channel)
    {
        var type = AggPanSearch.Detection.NetDiskDetector.GetLinkType(url);
        return new SearchResult
        {
            Title = title,
            Content = title,
            Channel = channel,
            Tags = tags.ToList(),
            Links = new List<Link>
            {
                new()
                {
                    Type = type,
                    Url = url,
                    Password = string.IsNullOrEmpty(password) ? null : password,
                },
            },
            Datetime = DateTime.UtcNow.AddHours(-1),
            UniqueId = title + url,
        };
    }

    // 便于序列化/调试
    public string ToJson() => JsonSerializer.Serialize(Dataset);
}