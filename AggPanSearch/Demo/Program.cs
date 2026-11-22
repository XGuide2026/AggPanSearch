using AggPanSearchServiceAlias = AggPanSearch.AggPanSearchService;

// AggPanSearch 简单演示：创建默认服务，搜索一次并输出结果数。
var keyword = args.Length > 0 ? args[0] : "凡人修仙传";
Console.WriteLine($"正在搜索：{keyword}");

using var search = AggPanSearchServiceAlias.CreateDefault(c =>
{
    c.Log = Console.WriteLine;
    c.CacheTtlMinutes = 30;
});

var response = await search.SearchAsync(keyword, r => r.Source("all").ResultType("merge"));
Console.WriteLine($"搜索完成：共 {response.Total} 条结果");