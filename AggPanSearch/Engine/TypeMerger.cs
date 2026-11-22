using AggPanSearch.Models;

namespace AggPanSearch.Engine;

/// <summary>
/// 按网盘类型分组归并链接，生成 MergedByType。
/// 对应 Go 项目 search_service.go 的 mergeResultsByType 逻辑。
/// </summary>
public static class TypeMerger
{
    /// <summary>
    /// 把搜索结果按网盘类型分组，返回按展示顺序排列的 MergedLinks。
    /// 与 Go 原版 mergeResultsByType 对齐：
    /// 1) 先按链接 URL 去重（uniqueLinks），同一 URL 只保留一条，Datetime 更新的替换旧的；
    /// 2) 对每个链接执行关键词过滤（标题或正文命中任一即保留），避免把不相关的链接带进来；
    /// 3) 按原始 results 顺序收集唯一链接，再按网盘类型分组，保持展示顺序稳定。
    /// </summary>
    /// <param name="results">排序后的搜索结果。</param>
    /// <param name="keyword">搜索关键词，用于链接级关键词过滤（对应 Go 的 keyword 参数）。</param>
    /// <param name="displayOrder">展示时网盘类型的排列顺序。</param>
    public static MergedLinks MergeByType(IReadOnlyList<SearchResult> results,
        string? keyword = null, IEnumerable<string>? displayOrder = null)
    {
        var lowerKeyword = keyword?.Trim().ToLowerInvariant() ?? string.Empty;
        var order = (displayOrder ?? CloudTypes.DisplayOrder).ToList();

        // 链接级去重：URL -> MergedLink（对应 Go 的 uniqueLinks）
        var unique = new Dictionary<string, MergedLink>(StringComparer.OrdinalIgnoreCase);
        // URL -> 网盘类型（用于后续分组）
        var linkTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            if (r.Links is null)
                continue;

            var sourceLabel = string.IsNullOrEmpty(r.Channel)
                ? string.Empty
                : (r.Channel.StartsWith("tg:", StringComparison.OrdinalIgnoreCase) || r.Channel.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)
                    ? r.Channel
                    : $"tg:{r.Channel}");

            foreach (var link in r.Links)
            {
                if (string.IsNullOrEmpty(link.Url))
                    continue;

                // 关键词过滤：标题或正文命中任一即保留（对应 Go 的 titleMatched/contentMatched）
                if (lowerKeyword.Length > 0)
                {
                    var candidateTitle = string.IsNullOrEmpty(link.WorkTitle) ? r.Title : link.WorkTitle;
                    // 用 OrdinalIgnoreCase 直接匹配，避免对每个候选字符串 ToLower 的重复分配；标题或正文命中任一即保留
                    var titleMatched = !string.IsNullOrEmpty(candidateTitle)
                        && candidateTitle.IndexOf(lowerKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    var contentMatched = !titleMatched
                        && !string.IsNullOrEmpty(r.Content)
                        && r.Content.IndexOf(lowerKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!titleMatched && !contentMatched)
                        continue; // 丢弃不匹配的链接
                }

                var type = string.IsNullOrEmpty(link.Type) ? CloudTypes.Others : link.Type.ToLowerInvariant();
                var note = BuildNote(r, link);

                // 优先使用链接自身时间，其次使用消息时间（对应 Go 的 linkDatetime 逻辑）
                var dt = link.Datetime ?? (r.Datetime == default ? (DateTime?)null : r.Datetime);

                var merged = new MergedLink
                {
                    Url = link.Url,
                    Password = link.Password,
                    Note = note,
                    Datetime = dt,
                    Source = sourceLabel,
                    Images = r.Images,
                };

                // URL 去重：仅当新的链接时间更晚时才替换（对应 Go 的 uniqueLinks 逻辑）
                if (unique.TryGetValue(link.Url, out var existing))
                {
                    if (IsNewer(dt, existing.Datetime))
                    {
                        unique[link.Url] = merged;
                        linkTypeMap[link.Url] = type;
                    }
                }
                else
                {
                    unique[link.Url] = merged;
                    linkTypeMap[link.Url] = type;
                }
            }
        }

        // 保持原始 results 顺序收集唯一链接，再按类型分组（对应 Go 的 orderedLinks + linkTypeMap）
        var orderedLinks = new List<MergedLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (r.Links is null)
                continue;
            foreach (var link in r.Links)
            {
                if (string.IsNullOrEmpty(link.Url) || !unique.ContainsKey(link.Url))
                    continue;
                if (seen.Add(link.Url))
                    orderedLinks.Add(unique[link.Url]);
            }
        }

        var map = new Dictionary<string, List<MergedLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in orderedLinks)
        {
            var type = linkTypeMap.TryGetValue(m.Url, out var t) ? t : CloudTypes.Others;
            if (!map.TryGetValue(type, out var list))
            {
                list = new List<MergedLink>();
                map[type] = list;
            }
            list.Add(m);
        }

        var mergedLinks = new MergedLinks();
        foreach (var type in order)
        {
            if (map.TryGetValue(type, out var list))
                mergedLinks[type] = list;
        }
        // 补充未在展示顺序中的类型
        foreach (var (type, list) in map)
        {
            if (!mergedLinks.ContainsKey(type))
                mergedLinks[type] = list;
        }
        return mergedLinks;
    }

    /// <summary>判断新时间是否严格晚于旧时间（都为 null 时为 false，对应 Go 的 After 语义）。</summary>
    private static bool IsNewer(DateTime? fresh, DateTime? old)
        => fresh.HasValue && old.HasValue && fresh.Value > old.Value;

    /// <summary>
    /// 构建链接备注：优先 WorkTitle，其次消息标题，并对"简介/描述"关键词之后的冗余内容做裁剪。
    /// 对应 Go 的 note 赋值与 util.CutTitleByKeywords。
    /// </summary>
    private static string BuildNote(SearchResult r, Link link)
    {
        var title = !string.IsNullOrEmpty(link.WorkTitle) ? link.WorkTitle : r.Title;
        if (string.IsNullOrEmpty(title))
            return string.Empty;
        return CutTitleByKeywords(title.Trim());
    }

    private static string CutTitleByKeywords(string title)
    {
        foreach (var kw in new[] { "简介", "描述" })
        {
            var idx = title.IndexOf(kw, StringComparison.Ordinal);
            if (idx > 0)
                return title[..idx].Trim();
        }
        return title;
    }
}