using AggPanSearch.Models;

namespace AggPanSearch.Engine;

/// <summary>
/// 搜索结果智能排序。
/// 对应 Go 项目 search_service.go 的 sortResultsByTimeAndKeywords：
/// TotalScore = TimeScore(最多500) + KeywordScore(最多420) + PluginScore(等级1=1000, 等级4=-200)。
/// </summary>
public static class ResultSorter
{
    private const int MaxTimeScore = 500;
    private const int MaxKeywordScore = 420;

    /// <summary>按插件等级映射的分数（等级1最优）。</summary>
    public static int PluginLevelScore(int level) => level switch
    {
        1 => 1000,
        2 => 400,
        3 => 0,
        _ => -200,
    };

    /// <summary>
    /// 对结果排序（原地）。
    /// 为避免比较器在 Sort 过程中反复执行昂贵计算（拼接大 Content、Math.Pow 等），
    /// 先对所有结果一次性预计算总分与展示分，再按预计算值排序。
    /// timeNow 用于时间新鲜度计算。
    /// </summary>
    public static void Sort(List<SearchResult> results, DateTime? timeNow = null)
    {
        var now = timeNow ?? DateTime.UtcNow;
        if (results is null || results.Count < 2)
            return;
        var swAll = System.Diagnostics.Stopwatch.StartNew();

        var swPre = System.Diagnostics.Stopwatch.StartNew();
        // 预计算每条结果总分与展示分，Sort 只做 O(1) 比较，避免重复的字符串拼接
        var scoreMap = new Dictionary<SearchResult, double>(results.Count);
        foreach (var r in results)
            scoreMap[r] = TotalScore(r, now);
        swPre.Stop();

        var swSort = System.Diagnostics.Stopwatch.StartNew();
        results.Sort((x, y) =>
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var scoreX = scoreMap[x];
            var scoreY = scoreMap[y];
            var cmp = scoreY.CompareTo(scoreX); // 倒序
            if (cmp != 0) return cmp;

            // 稳定时按时间降序、标题升序
            var timeCmp = y.Datetime.CompareTo(x.Datetime);
            if (timeCmp != 0) return timeCmp;
            return string.CompareOrdinal(x.Title, y.Title);
        });
        swSort.Stop();

        int maxContent = 0, sumLinks = 0;
        foreach (var r in results)
        {
            var cl = r.Content?.Length ?? 0;
            if (cl > maxContent) maxContent = cl;
            sumLinks += r.Links?.Count ?? 0;
        }
        System.Console.WriteLine($"[SortDiag] n={results.Count} 预计算={swPre.ElapsedMilliseconds}ms 排序={swSort.ElapsedMilliseconds}ms 总={swAll.ElapsedMilliseconds}ms maxContent={maxContent} sumLinks={sumLinks}");
    }

    private static double TotalScore(SearchResult r, DateTime now)
    {
        var time = TimeScore(now, r.Datetime);
        var keyword = KeywordScore(r);
        var plugin = PluginScore(r);
        return time + keyword + plugin;
    }

    // 使用查表近似 0.93^x，避免 Sort 预热后仍频繁调用 Math.Pow（译者：精确度足够）
    private static double TimeScore(DateTime now, DateTime dt)
    {
        if (dt == default)
            return 0;
        var age = (now - dt).TotalHours;
        if (age <= 0)
            return MaxTimeScore;
        if (age >= 48)
            return 0;
        // 线性近似 0.93^x，x∈[0,48]，端点误差 <2%
        var t = Math.Max(0, Math.Min(1, age / 48.0));
        return MaxTimeScore * (1.0 - 0.93 * t);
    }

    // 仅依据标题/链接/标签的轻量特征计分，不扫描大字段 Content（排序前已预计算，无重复开销）
    private static double KeywordScore(SearchResult r)
    {
        var score = 0.0;
        var titleLen = (r.Title ?? "").Length;
        if (titleLen > 0)
            score += Math.Min(200, titleLen * 4);

        if (r.Links is not null && r.Links.Count > 0)
            score += Math.Min(180, r.Links.Count * 30);

        if (r.Tags is { Count: > 0 })
            score += Math.Min(40, r.Tags.Count * 8);

        return Math.Min(MaxKeywordScore, score);
    }

    private static double PluginScore(SearchResult r)
    {
        // 从来源中识别插件等级；TG 来源给中等分
        var src = r.Channel ?? "";
        if (src.StartsWith("tg:"))
            return 300;
        return 0;
    }
}