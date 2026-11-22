using AggPanSearch.Models;

namespace AggPanSearch.Engine;

/// <summary>
/// 搜索结果归并：多来源去重、按链接质量合并。
/// 对应 Go 项目 search_service.go 的 mergeSearchResults 逻辑。
/// </summary>
public static class ResultMerger
{
    /// <summary>
    /// 合并多来源结果，以 UniqueID/MessageID/标题+频道 作为唯一键，同键保留信息完整度高的。
    /// </summary>
    public static List<SearchResult> Merge(IEnumerable<SearchResult> sources)
    {
        var map = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sources)
        {
            if (s is null || (s.Links is null || s.Links.Count == 0))
                continue;

            var key = BuildKey(s);
            if (string.IsNullOrEmpty(key))
                continue;

            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = s;
                continue;
            }

            // 同键时保留完整度更高的
            if (CalculateCompletenessScore(s) > CalculateCompletenessScore(existing))
                map[key] = s;
        }

        return map.Values.ToList();
    }

    private static string BuildKey(SearchResult r)
    {
        if (!string.IsNullOrEmpty(r.UniqueId))
            return "u:" + r.UniqueId;
        if (!string.IsNullOrEmpty(r.MessageId))
            return "m:" + r.MessageId;
        if (!string.IsNullOrEmpty(r.Title) && !string.IsNullOrEmpty(r.Channel))
            return $"t:{r.Channel}:{HashTitle(r.Title)}";
        return string.Empty;
    }

    private static string HashTitle(string title)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(title)))[..16];

    /// <summary>
    /// 信息完整度评分：UniqueID +10，每个链接 +5，内容 +3，标题长度，标签。
    /// 对应 Go 项目 calculateCompletenessScore。
    /// </summary>
    public static int CalculateCompletenessScore(SearchResult r)
    {
        var score = 0;
        if (!string.IsNullOrEmpty(r.UniqueId)) score += 10;
        if (r.Links != null) score += r.Links.Count * 5;
        if (!string.IsNullOrEmpty(r.Content)) score += 3;
        if (!string.IsNullOrEmpty(r.Title)) score += Math.Min(5, r.Title.Length / 4);
        if (r.Tags != null && r.Tags.Count > 0) score += 2;
        return score;
    }

    /// <summary>
    /// 过滤结果：按 include（OR）/ exclude（AND）与网盘类型过滤。
    /// </summary>
    public static List<SearchResult> Filter(List<SearchResult> results, SearchFilter? filter, IReadOnlyList<string>? cloudTypes)
    {
        if (filter is null && (cloudTypes is null || cloudTypes.Count == 0))
            return results;

        var lowerTypes = cloudTypes?.Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

        return results.Where(r =>
        {
            // 网盘类型过滤
            if (lowerTypes is not null && lowerTypes.Count > 0)
            {
                var hitType = r.Links?.Any(l => lowerTypes.Contains(l.Type.ToLowerInvariant())) ?? false;
                if (!hitType)
                    return false;
            }

            var title = r.Title ?? "";
            var content = r.Content ?? "";

            // Exclude（AND 关系，命中任一则排除）
            if (filter?.Exclude is { Count: > 0 })
            {
                foreach (var ex in filter.Exclude)
                {
                    if (!string.IsNullOrEmpty(ex) &&
                        (title.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         content.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0))
                        return false; // 命中任一排除词即排除
                }
            }

            // Include（OR 关系，命中任一则保留）
            if (filter?.Include is { Count: > 0 })
            {
                var any = false;
                foreach (var inc in filter.Include)
                {
                    if (!string.IsNullOrEmpty(inc) &&
                        (title.IndexOf(inc, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         content.IndexOf(inc, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        any = true;
                        break;
                    }
                }
                if (!any)
                    return false;
            }

            return true;
        }).ToList();
    }
}