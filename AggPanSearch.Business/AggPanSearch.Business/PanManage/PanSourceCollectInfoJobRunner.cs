using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：资源文件分析队列的实时进度状态（进程内静态，供前端轮询展示）。
    /// 图片下载批处理完成后由 <see cref="PanSourceCollectInfoJobRunner.Enqueue"/> 把该批记录入队，
    /// 队列工作线程逐条打开网盘分享页解析三字段写回采集表，统计累加到这里。
    /// 各计数单位均为「资源条数」。
    /// </summary>
    public sealed class PanSourceCollectInfoQueueState
    {
        public const string StatusIdle = "idle";
        public const string StatusRunning = "running";

        /// <summary>idle（队列空闲）/ running（队列处理中）。</summary>
        public string Status { get; set; } = StatusIdle;

        /// <summary>当前周期应分析的资源总条数（入队时累加）。</summary>
        public int Total { get; set; }

        /// <summary>已处理的资源条数（含成功 / 跳过 / 失败）。</summary>
        public int Done { get; set; }

        /// <summary>成功解析并写回的资源条数。</summary>
        public int Succeeded { get; set; }

        /// <summary>已分析过 / 无网盘链接而跳过的资源条数。</summary>
        public int Skipped { get; set; }

        /// <summary>打开网页失败等未能解析的资源条数。</summary>
        public int Failed { get; set; }

        /// <summary>给前端的提示文案。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>最近一次统计更新时间。</summary>
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        public PanSourceCollectInfoQueueState Clone()
        {
            return (PanSourceCollectInfoQueueState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 描 述：资源文件分析执行器（资源文件分析队列）。
    /// 图片下载批处理完成后，把该批已入库的采集记录入队；队列工作线程逐条调用
    /// <see cref="QuarkShareInfoAnalyzer.AnalyzeAsync"/> 打开夸克分享页，解析 .info-stat
    /// 三字段并只回写 file_count / file_size / share_validity_period 三列。
    /// 已分析过或无网盘链接的记录跳过；单条失败只计数不中断队列。与截图共用浏览器进程。
    /// </summary>
    public sealed class PanSourceCollectInfoJobRunner
    {
        /// <summary>资源文件分析队列单次批量。</summary>
        private const int QueueBatchSize = 20;

        /// <summary>单条打开网盘页面为耗时热点，队列串行处理避免挤压截图/采集。</summary>
        private const int AnalyzeIntervalMs = 300;

        private static readonly ConcurrentQueue<long> PendingIds = new ConcurrentQueue<long>();

        /// <summary>已入队尚未出队的 Id 集合，防止同一记录被重复入队（出队时移除，允许后续重试/补分析）。</summary>
        private static readonly ConcurrentDictionary<long, byte> QueuedIds = new ConcurrentDictionary<long, byte>();

        private static int queueWorkerRunning;

        /// <summary>资源文件分析队列状态（锁保护，前端轮询读取）。</summary>
        private static readonly object AutoQueueStateLock = new object();
        private static PanSourceCollectInfoQueueState autoQueueState = new PanSourceCollectInfoQueueState();

        private readonly PanSourceCollectService panSourceCollectService = new PanSourceCollectService();

        /// <summary>获取资源文件分析队列的实时进度状态（返回副本，调用方可自由读取）。</summary>
        public static PanSourceCollectInfoQueueState GetInfoQueueState()
        {
            lock (AutoQueueStateLock)
            {
                return autoQueueState.Clone();
            }
        }

        /// <summary>
        /// 图片下载批处理完成后把该批 Id 投递到资源文件分析队列（按 Id 去重，已入队的不重复投递）。
        /// 若队列在运行中则直接并入，否则唤醒工作线程并重置本周期统计。
        /// </summary>
        public static void Enqueue(IEnumerable<long> ids)
        {
            int added = 0;
            if (ids != null)
            {
                foreach (long id in ids)
                {
                    if (id > 0 && QueuedIds.TryAdd(id, 0))
                    {
                        PendingIds.Enqueue(id);
                        added++;
                    }
                }
            }
            if (added > 0 && Interlocked.CompareExchange(ref queueWorkerRunning, 1, 0) == 0)
            {
                // 从空闲唤醒：重置本周期的累计统计
                ResetAutoQueueState();
                _ = Task.Run(DrainQueueAsync, CancellationToken.None);
            }
            if (added > 0)
            {
                UpdateAutoQueueState(state =>
                {
                    state.Total += added;
                });
            }
        }

        /// <summary>重置资源文件分析队列统计并标记为运行中（队列从空闲唤醒时调用）。</summary>
        private static void ResetAutoQueueState()
        {
            lock (AutoQueueStateLock)
            {
                autoQueueState.Status = PanSourceCollectInfoQueueState.StatusRunning;
                autoQueueState.Total = 0;
                autoQueueState.Done = 0;
                autoQueueState.Succeeded = 0;
                autoQueueState.Skipped = 0;
                autoQueueState.Failed = 0;
                autoQueueState.Message = "资源文件分析已启动，正在打开网盘链接…";
                autoQueueState.UpdateTime = DateTime.Now;
            }
        }

        /// <summary>在锁内更新资源文件分析队列状态，并刷新更新时间。</summary>
        private static void UpdateAutoQueueState(Action<PanSourceCollectInfoQueueState> update)
        {
            lock (AutoQueueStateLock)
            {
                update(autoQueueState);
                autoQueueState.UpdateTime = DateTime.Now;
            }
        }

        /// <summary>资源文件分析队列工作线程：循环取空队列后退出。</summary>
        private static async Task DrainQueueAsync()
        {
            try
            {
                PanSourceCollectInfoJobRunner runner = new PanSourceCollectInfoJobRunner();
                while (true)
                {
                    List<long> batch = new List<long>();
                    while (batch.Count < QueueBatchSize && PendingIds.TryDequeue(out long id))
                    {
                        batch.Add(id);
                        QueuedIds.TryRemove(id, out _);
                    }
                    if (batch.Count == 0)
                    {
                        break;
                    }
                    try
                    {
                        await runner.ProcessBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 单批失败不影响采集与后续队列
                        LogHelper.Error(ex);
                        UpdateAutoQueueState(state =>
                        {
                            state.Message = "本批信息分析异常，继续处理后续记录";
                            state.UpdateTime = DateTime.Now;
                        });
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref queueWorkerRunning, 0);
                // 退出瞬间可能有新任务入队：重新占用标志并续跑下一轮（不 Reset，避免清空本周期统计）
                if (!PendingIds.IsEmpty)
                {
                    if (Interlocked.CompareExchange(ref queueWorkerRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(DrainQueueAsync, CancellationToken.None);
                    }
                }
                else
                {
                    UpdateAutoQueueState(state =>
                    {
                        state.Status = PanSourceCollectInfoQueueState.StatusIdle;
                        state.Message = state.Done > 0
                            ? $"资源文件分析完成：共 {state.Total} 条，有效 {state.Succeeded} 条，跳过 {state.Skipped} 条，无效 {state.Failed} 条"
                            : "资源文件分析队列已清空";
                        state.UpdateTime = DateTime.Now;
                    });
                }
            }
        }

        /// <summary>逐条处理一批记录：先按是否已分析/有无链接过滤，再打开网盘页面解析三字段并回写。</summary>
        private async Task ProcessBatchAsync(List<long> batch, CancellationToken token)
        {
            List<PanSourceCollectEntity> records = await panSourceCollectService.GetListByIds(batch).ConfigureAwait(false);
            List<PanSourceCollectEntity> toSkip = new List<PanSourceCollectEntity>();
            List<PanSourceCollectEntity> toAnalyze = new List<PanSourceCollectEntity>();
            foreach (PanSourceCollectEntity record in records)
            {
                if (record == null || record.Id == null || record.Id.Value <= 0)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(record.ResourceUrl) || IsAlreadyAnalyzed(record))
                {
                    toSkip.Add(record);
                }
                else
                {
                    toAnalyze.Add(record);
                }
            }

            foreach (PanSourceCollectEntity record in toSkip)
            {
                UpdateAutoQueueState(state =>
                {
                    state.Done++;
                    state.Skipped++;
                });
            }

            if (toAnalyze.Count > 0)
            {
                UpdateAutoQueueState(state =>
                {
                    state.Message = $"正在分析资源信息：待分析 {toAnalyze.Count} 条…";
                });
            }

            foreach (PanSourceCollectEntity record in toAnalyze)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    (PanSourceInfoStat stat, string error) = await QuarkShareInfoAnalyzer
                        .AnalyzeAsync(record.ResourceUrl, null, token)
                        .ConfigureAwait(false);

                    if (error.Length == 0 && stat != null && stat.HasAny)
                    {
                        // 分析成功（分享页存在 .info-stat 统计区）：回写三字段，并标记链接有效
                        await panSourceCollectService
                            .UpdateAnalyzeInfo(record.Id.Value, stat.FileCount, stat.FileSize, stat.ShareValidityPeriod, 1)
                            .ConfigureAwait(false);
                        UpdateAutoQueueState(state =>
                        {
                            state.Done++;
                            state.Succeeded++;
                        });
                    }
                    else
                    {
                        LogHelper.Info($"资源文件分析失败 Id={record.Id.Value}，原因：{error}");
                        // 分析失败（提取码 / 失效 / 超时 / 非夸克 / 无统计区）：标记链接无效
                        await panSourceCollectService
                            .UpdateLinkValid(record.Id.Value, 0)
                            .ConfigureAwait(false);
                        UpdateAutoQueueState(state =>
                        {
                            state.Done++;
                            state.Failed++;
                        });
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex);
                    // 分析异常同属未成功检测：标记链接无效
                    await panSourceCollectService
                        .UpdateLinkValid(record.Id.Value, 0)
                        .ConfigureAwait(false);
                    UpdateAutoQueueState(state =>
                    {
                        state.Done++;
                        state.Failed++;
                    });
                }
                finally
                {
                    await Task.Delay(AnalyzeIntervalMs, token).ConfigureAwait(false);
                }
            }

            UpdateAutoQueueState(state =>
            {
                state.Message = toSkip.Count > 0
                    ? $"本批处理完成：有效 {state.Succeeded} 条，跳过 {state.Skipped} 条，无效 {state.Failed} 条"
                    : $"本批处理完成：有效 {state.Succeeded} 条，无效 {state.Failed} 条";
            });
        }

        /// <summary>已分析过（文件个数或文件大小已回写）的记录视为无需重复分析。</summary>
        private static bool IsAlreadyAnalyzed(PanSourceCollectEntity record)
        {
            return record.FileCount != null && record.FileCount.Value > 0
                || record.FileSize != null && record.FileSize.Value > 0;
        }
    }
}