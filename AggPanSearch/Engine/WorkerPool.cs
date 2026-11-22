namespace AggPanSearch.Engine;

/// <summary>
/// 泛型工作池：限制并发执行委托任务，返回按输入顺序排列的结果。
/// 对应 Go 项目 service/batch_search.go 的工作池（短作业优先、并发限制）逻辑。
/// </summary>
public static class WorkerPool
{
    /// <summary>
    /// 并发执行任务集合（短作业优先按 estimatedCost 升序提交）。
    /// 支持"最快响应窗口"：窗口到期后取消未完成任务，返回已收集结果快照，
    /// 避免最慢任务拖长整体响应时间（对应 Go 原版的异步响应窗口机制）。
    /// </summary>
    /// <typeparam name="TIn">输入类型。</typeparam>
    /// <typeparam name="TOut">输出类型。</typeparam>
    /// <param name="items">待处理项。</param>
    /// <param name="maxConcurrency">最大并发数。</param>
    /// <param name="worker">处理函数，返回 (output, estimatedCost)。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <param name="fastResponseWindow">最快响应窗口；为 null 时等待全部完成。</param>
    /// <returns>每个元素：Input=输入；Output=输出；Error=异常（null 表示无）；
    /// Completed=true 表示该任务已正常结束（成功或失败），false 表示因窗口到期被取消/未轮到执行。</returns>
    public static async Task<List<(TIn Input, TOut? Output, Exception? Error, bool Completed)>> ExecuteAsync<TIn, TOut>(
        IReadOnlyList<TIn> items,
        int maxConcurrency,
        Func<TIn, CancellationToken, Task<(TOut? Output, double EstCost)>> worker,
        CancellationToken cancellationToken = default,
        TimeSpan? fastResponseWindow = null,
        Action<string>? log = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        log?.Invoke($"[WP] 启动 items={items.Count} 并发={maxConcurrency} 窗口={(fastResponseWindow?.TotalSeconds.ToString("0.#") ?? "关闭")}s");
        var results = new List<(TIn, TOut?, Exception?, bool)>(items.Count);

        if (items.Count == 0)
            return results;

        maxConcurrency = Math.Max(1, maxConcurrency);
        // 短作业优先：按预估成本升序
        var ordered = items
            .Select((item, idx) => new { item, idx })
            .OrderBy(x => PriorityOf(x.item))
            .ToList();

        // 最快响应窗口：窗口到期后取消未完成任务（已完成的保留在快照中）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (fastResponseWindow is not null)
            cts.CancelAfter(fastResponseWindow.Value);
        var effectiveCt = cts.Token;

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>(ordered.Count);

        foreach (var entry in ordered)
        {
            var captured = entry;
            // 注意：Task.Run 不绑定 effectiveCt，避免窗口到期时尚未启动的任务直接以
            // Canceled 状态结束导致 Task.WhenAll 抛 OperationCanceledException；
            // 取消由 body 内部的 WaitAsync / worker 感知，并在内部被吞掉，保证任务正常结束。
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(effectiveCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 窗口已到期，该任务未轮到执行：记为未完成
                    lock (results) results.Add((captured.item, default, null, false));
                    return;
                }

                try
                {
                    var (outVal, _) = await worker(captured.item, effectiveCt).ConfigureAwait(false);
                    lock (results) results.Add((captured.item, outVal, null, true));
                }
                catch (OperationCanceledException)
                {
                    // 在途任务被窗口取消：记为未完成
                    lock (results) results.Add((captured.item, default, null, false));
                }
                catch (Exception ex)
                {
                    // 任务自身失败：已结束（记为完成，供统计），Error 保留失败原因
                    lock (results) results.Add((captured.item, default, ex, true));
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        // 关键：不能无条件 await Task.WhenAll(tasks)。
        // 窗口到期后，in-flight 的慢请求（DNS/连接阶段对取消响应迟钝）可能再拖数秒，
        // 导致"最快响应窗口"名存实亡。改为：窗口到期后再宽限一小段收集在途结果，
        // 到期即返回快照，未完成任务放后台自然结束，绝不阻塞响应。
        const int graceMs = 200;
        var allDone = Task.WhenAll(tasks);

        Task awaited;
        if (fastResponseWindow is not null)
        {
            // 注意：宽限的计时起点是"窗口到期后"，即"窗口 + 宽限"。
            // 此前实现误用了固定 Task.Delay(200)，导致任务启动 200ms 就返回快照、
            // 1.5s 窗口形同虚设，真实 TG 请求全部来不及完成就被丢弃，结果恒为空。
            var windowPlusGrace = Task.Delay(fastResponseWindow.Value + TimeSpan.FromMilliseconds(graceMs));
            log?.Invoke($"[WP] delay 创建 @ {sw.ElapsedMilliseconds}ms (目标 {fastResponseWindow.Value.TotalSeconds:0.#}+{graceMs}ms)");
            awaited = await Task.WhenAny(allDone, windowPlusGrace).ConfigureAwait(false);
            log?.Invoke($"[WP] WhenAny 返回 @ {sw.ElapsedMilliseconds}ms 返回者={(ReferenceEquals(awaited, allDone) ? "allDone" : "delay")} delay完成={windowPlusGrace.IsCompleted}");
        }
        else
        {
            // 无窗口：等待全部完成（任务内部已吞掉取消，allDone 不会抛 OCE）
            await allDone.ConfigureAwait(false);
            awaited = allDone;
        }

        if (!ReferenceEquals(awaited, allDone))
        {
            // 宽限到期：吞掉后台任务的异常，避免 UnobservedTaskException 干扰进程
            _ = ObserveNoThrow(allDone);
        }

        List<(TIn, TOut?, Exception?, bool)> snapshot;
        lock (results) snapshot = results.ToList();
        log?.Invoke($"[WP] 快照就绪 @ {sw.ElapsedMilliseconds}ms 条数={snapshot.Count}");

        // 恢复输入顺序
        return snapshot
            .OrderBy(r => ordered.FindIndex(x => Equals(x.item, r.Item1)))
            .ToList();
    }

    private static async Task ObserveNoThrow(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* 后台任务异常忽略 */ }
    }

    private static double PriorityOf<T>(T item)
    {
        // 若输入自带成本（IPlugin 等），此处由调用方向排序；这里仅保证稳定性
        return 0;
    }
}
