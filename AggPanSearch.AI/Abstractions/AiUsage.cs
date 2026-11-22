namespace AggPanSearch.AI;

/// <summary>token 用量，用于成本观察与日志记录。</summary>
public sealed class AiUsage
{
    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int TotalTokens { get; init; }

    public override string ToString()
    {
        return $"prompt={PromptTokens}, completion={CompletionTokens}, total={TotalTokens}";
    }
}
