namespace AggPanSearch.AI;

/// <summary>
/// 可选能力：查询账户可用的模型列表。并非所有厂商都提供 /models，
/// 调用方需用 <c>client is IAiModelCatalog</c> 判断后再使用。
/// </summary>
public interface IAiModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default);
}
