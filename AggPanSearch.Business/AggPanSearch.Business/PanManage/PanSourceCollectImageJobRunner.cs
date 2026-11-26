using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：图片补图任务的内存状态（后台下载 + 前端轮询进度）。
    /// 不做持久化，进程重启后任务丢失，重新触发补图即可。
    /// </summary>
    public class PanSourceCollectImageJobState
    {
        public string JobId { get; set; } = string.Empty;

        /// <summary>running / succeeded / failed / canceled。</summary>
        public string Status { get; set; } = PanSourceCollectImageJobManager.StatusRunning;

        /// <summary>本次任务涉及的记录总数。</summary>
        public int Total { get; set; }

        /// <summary>已处理记录数。</summary>
        public int Done { get; set; }

        /// <summary>已回写本地图片的记录数。</summary>
        public int Updated { get; set; }

        /// <summary>本地图片已齐全而跳过的记录数。</summary>
        public int Skipped { get; set; }

        /// <summary>被剔除的失效图片数量。</summary>
        public int FailedImages { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime? EndTime { get; set; }

        public PanSourceCollectImageJobState Clone()
        {
            return (PanSourceCollectImageJobState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 描 述：图片补图任务注册表（进程内静态单例）。
    /// 补图属于 IO 密集操作且只写图片两列，手动补图全局只允许运行 1 个，避免抢占带宽与磁盘。
    /// </summary>
    public static class PanSourceCollectImageJobManager
    {
        public const string StatusRunning = "running";
        public const string StatusSucceeded = "succeeded";
        public const string StatusFailed = "failed";
        public const string StatusCanceled = "canceled";

        private const int MaxJobCount = 20;
        private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
        private static readonly object CreateLock = new object();

        private static readonly ConcurrentDictionary<string, JobEntry> Jobs =
            new ConcurrentDictionary<string, JobEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class JobEntry
        {
            public readonly object SyncRoot = new object();

            public PanSourceCollectImageJobState State = new PanSourceCollectImageJobState();

            public CancellationTokenSource Cts = new CancellationTokenSource();
        }

        /// <summary>创建补图任务并返回 JobId 与取消令牌；已有任务运行时返回空 JobId。</summary>
        public static string Create(out CancellationToken token, out string error)
        {
            token = CancellationToken.None;
            error = string.Empty;
            Cleanup();

            lock (CreateLock)
            {
                if (Jobs.Values.Any(GetIsRunning))
                {
                    error = "已有补图任务在运行，请等待完成后再试";
                    return string.Empty;
                }

                string jobId = Guid.NewGuid().ToString("N");
                JobEntry entry = new JobEntry
                {
                    State = new PanSourceCollectImageJobState
                    {
                        JobId = jobId,
                        Status = StatusRunning,
                        Message = "补图任务已启动",
                        StartTime = DateTime.Now,
                    },
                };
                Jobs[jobId] = entry;
                token = entry.Cts.Token;
                return jobId;
            }
        }

        public static PanSourceCollectImageJobState Get(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || !Jobs.TryGetValue(jobId, out JobEntry entry))
            {
                return null;
            }
            lock (entry.SyncRoot)
            {
                return entry.State.Clone();
            }
        }

        /// <summary>取最近启动且仍在运行的补图任务（页面刷新后恢复进度显示用）。</summary>
        public static PanSourceCollectImageJobState GetLatestRunning()
        {
            PanSourceCollectImageJobState latest = null;
            foreach (JobEntry entry in Jobs.Values)
            {
                lock (entry.SyncRoot)
                {
                    if (entry.State.Status != StatusRunning)
                    {
                        continue;
                    }
                    if (latest == null || entry.State.StartTime > latest.StartTime)
                    {
                        latest = entry.State.Clone();
                    }
                }
            }
            return latest;
        }

        public static bool Update(string jobId, Action<PanSourceCollectImageJobState> update)
        {
            if (update == null || string.IsNullOrWhiteSpace(jobId) || !Jobs.TryGetValue(jobId, out JobEntry entry))
            {
                return false;
            }
            lock (entry.SyncRoot)
            {
                if (entry.State.Status != StatusRunning)
                {
                    return false;
                }
                update(entry.State);
            }
            return true;
        }

        public static void Finish(string jobId, string status, string message)
        {
            if (string.IsNullOrWhiteSpace(jobId) || !Jobs.TryGetValue(jobId, out JobEntry entry))
            {
                return;
            }
            lock (entry.SyncRoot)
            {
                entry.State.Status = status;
                entry.State.Message = message ?? string.Empty;
                entry.State.EndTime = DateTime.Now;
            }
        }

        public static bool Cancel(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || !Jobs.TryGetValue(jobId, out JobEntry entry))
            {
                return false;
            }
            lock (entry.SyncRoot)
            {
                if (entry.State.Status != StatusRunning)
                {
                    return false;
                }
                entry.State.Message = "正在取消补图…";
            }
            try
            {
                entry.Cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private static bool GetIsRunning(JobEntry entry)
        {
            lock (entry.SyncRoot)
            {
                return entry.State.Status == StatusRunning;
            }
        }

        private static void Cleanup()
        {
            DateTime now = DateTime.Now;
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, JobEntry> pair in Jobs)
            {
                lock (pair.Value.SyncRoot)
                {
                    if (pair.Value.State.Status == StatusRunning)
                    {
                        continue;
                    }
                    DateTime? endTime = pair.Value.State.EndTime;
                    if (endTime == null || now - endTime.Value > Retention)
                    {
                        expired.Add(pair.Key);
                    }
                }
            }
            Remove(expired);

            if (Jobs.Count > MaxJobCount)
            {
                List<string> overflow = Jobs.Values
                    .Select(t =>
                    {
                        lock (t.SyncRoot)
                        {
                            return new { t.State.JobId, t.State.StartTime };
                        }
                    })
                    .OrderBy(t => t.StartTime)
                    .Take(Jobs.Count - MaxJobCount)
                    .Select(t => t.JobId)
                    .ToList();
                Remove(overflow);
            }
        }

        private static void Remove(List<string> jobIds)
        {
            foreach (string jobId in jobIds)
            {
                if (Jobs.TryRemove(jobId, out JobEntry removed))
                {
                    try
                    {
                        removed.Cts.Dispose();
                    }
                    catch
                    {
                        // 忽略释放异常
                    }
                }
            }
        }
    }

    /// <summary>
    /// 描 述：自动补图队列的实时进度状态（进程内静态，供前端轮询展示）。
    /// 采集入库后由 <see cref="PanSourceCollectImageJobRunner.Enqueue"/> 入队，
    /// 队列工作线程逐批处理后把统计累加到这里，页面通过接口读取并展示进度。
    /// </summary>
    public sealed class PanSourceCollectAutoQueueState
    {
        public const string StatusIdle = "idle";
        public const string StatusRunning = "running";

        /// <summary>idle（队列空闲）/ running（队列处理中）。</summary>
        public string Status { get; set; } = StatusIdle;

        /// <summary>当前处理周期累计入队的记录数。</summary>
        public int Total { get; set; }

        /// <summary>当前处理周期已处理的记录数（含回写与跳过）。</summary>
        public int Processed { get; set; }

        /// <summary>已回写本地图片的记录数。</summary>
        public int Updated { get; set; }

        /// <summary>本地图片已齐全而跳过的记录数。</summary>
        public int Skipped { get; set; }

        /// <summary>被剔除的失效图片数量。</summary>
        public int FailedImages { get; set; }

        /// <summary>队列中剩余等待处理的记录数。</summary>
        public int Pending { get; set; }

        /// <summary>给前端的提示文案。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>最近一次统计更新时间。</summary>
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        public PanSourceCollectAutoQueueState Clone()
        {
            return (PanSourceCollectAutoQueueState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 描 述：图片补图任务执行器。
    /// 把记录中 collect_image_url 的远端图片下载到本机，并只回写 collect_image_url / image_url 两列；
    /// 下载失败的图片会从 collect_image_url 中剔除，因此任务结束后原图字段可能变短。
    /// 采集入库后走 <see cref="Enqueue"/> 队列自动补图，后台页面的“补图”按钮走 <see cref="Start"/> 并展示进度。
    /// </summary>
    public sealed class PanSourceCollectImageJobRunner
    {
        /// <summary>记录之间的并发数，记录内图片保持顺序下载以维持索引对应。</summary>
        private const int RecordConcurrency = 3;

        /// <summary>手动补图的默认条数。</summary>
        private const int DefaultLimit = 50;

        /// <summary>自动补图队列单次批量。</summary>
        private const int QueueBatchSize = 100;

        private static readonly ConcurrentQueue<long> PendingIds = new ConcurrentQueue<long>();
        private static int queueWorkerRunning;

        /// <summary>自动补图队列状态（锁保护，前端轮询读取）。</summary>
        private static readonly object AutoQueueStateLock = new object();
        private static PanSourceCollectAutoQueueState autoQueueState = new PanSourceCollectAutoQueueState();

        private readonly PanSourceCollectService panSourceCollectService = new PanSourceCollectService();

        /// <summary>获取自动补图队列的实时进度状态（返回副本，调用方可自由读取）。</summary>
        public static PanSourceCollectAutoQueueState GetAutoQueueState()
        {
            lock (AutoQueueStateLock)
            {
                return autoQueueState.Clone();
            }
        }

        /// <summary>采集入库后把新增记录投递到自动补图队列（不等待下载，也不占用手动补图任务）。</summary>
        public static void Enqueue(IEnumerable<long>? ids)
        {
            int added = 0;
            if (ids != null)
            {
                foreach (long id in ids)
                {
                    if (id > 0)
                    {
                        PendingIds.Enqueue(id);
                        added++;
                    }
                }
            }
            if (added > 0)
            {
                UpdateAutoQueueState(state => state.Pending += added);
            }
            if (Interlocked.CompareExchange(ref queueWorkerRunning, 1, 0) == 0)
            {
                // 从空闲唤醒：重置本周期的累计统计
                ResetAutoQueueState();
                _ = Task.Run(DrainQueueAsync, CancellationToken.None);
            }
        }

        /// <summary>重置自动补图队列统计并标记为运行中（队列从空闲唤醒时调用）。</summary>
        private static void ResetAutoQueueState()
        {
            lock (AutoQueueStateLock)
            {
                autoQueueState.Status = PanSourceCollectAutoQueueState.StatusRunning;
                autoQueueState.Total = 0;
                autoQueueState.Processed = 0;
                autoQueueState.Updated = 0;
                autoQueueState.Skipped = 0;
                autoQueueState.FailedImages = 0;
                autoQueueState.Message = "自动补图已启动，正在下载本地图片…";
                autoQueueState.UpdateTime = DateTime.Now;
            }
        }

        /// <summary>在锁内更新自动补图队列状态，并刷新更新时间。</summary>
        private static void UpdateAutoQueueState(Action<PanSourceCollectAutoQueueState> update)
        {
            lock (AutoQueueStateLock)
            {
                update(autoQueueState);
                autoQueueState.UpdateTime = DateTime.Now;
            }
        }

        /// <summary>启动手动补图任务：ids 非空时补指定记录，否则补缺少本地图片的前 limit 条。</summary>
        public static string Start(List<long> ids, int limit, out string error)
        {
            error = string.Empty;
            string jobId = PanSourceCollectImageJobManager.Create(out CancellationToken token, out error);
            if (jobId.Length == 0)
            {
                return string.Empty;
            }

            List<long> targetIds = ids?.Where(t => t > 0).Distinct().ToList() ?? new List<long>();
            int targetLimit = limit > 0 ? limit : DefaultLimit;
            PanSourceCollectImageJobRunner runner = new PanSourceCollectImageJobRunner();
            _ = Task.Run(
                () => runner.RunAsync(jobId, targetIds, targetLimit, token),
                CancellationToken.None);
            return jobId;
        }

        /// <summary>自动补图队列工作线程：循环取空队列后退出。</summary>
        private static async Task DrainQueueAsync()
        {
            try
            {
                PanSourceCollectImageJobRunner runner = new PanSourceCollectImageJobRunner();
                while (true)
                {
                    List<long> batch = new List<long>();
                    while (batch.Count < QueueBatchSize && PendingIds.TryDequeue(out long id))
                    {
                        batch.Add(id);
                    }
                    if (batch.Count == 0)
                    {
                        break;
                    }
                    UpdateAutoQueueState(state =>
                    {
                        state.Pending = Math.Max(0, state.Pending - batch.Count);
                        state.Total += batch.Count;
                    });
                    try
                    {
                        List<PanSourceCollectEntity> records =
                            await runner.panSourceCollectService.GetListByIds(batch).ConfigureAwait(false);
                        records = FilterRecords(records);
                        if (records.Count > 0)
                        {
                            UpdateAutoQueueState(state =>
                            {
                                state.Message = $"正在下载本地图片：本批 {records.Count} 条…";
                                state.UpdateTime = DateTime.Now;
                            });
                        }
                        ImageProcessResult result = new ImageProcessResult();
                        if (records.Count > 0)
                        {
                            await runner.ProcessAsync(
                                records, CancellationToken.None, result, null).ConfigureAwait(false);
                        }
                        UpdateAutoQueueState(state =>
                        {
                            state.Processed += result.Done;
                            state.Updated += result.Updated;
                            state.Skipped += result.Skipped;
                            state.FailedImages += result.FailedImages;
                            state.Message = records.Count > 0
                                ? $"本批处理完成：回写 {result.Updated} 条，跳过 {result.Skipped} 条，剔除失效图 {result.FailedImages} 张"
                                : "本批没有需要补图的记录";
                            state.UpdateTime = DateTime.Now;
                        });
                    }
                    catch (Exception ex)
                    {
                        // 自动补图失败不影响采集与后续队列
                        LogHelper.Error(ex);
                        UpdateAutoQueueState(state =>
                        {
                            state.Message = "本批处理异常，继续处理后续记录";
                            state.UpdateTime = DateTime.Now;
                        });
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref queueWorkerRunning, 0);
                // 退出瞬间可能有新任务入队：重新占用标志并续跑下一轮（不再走 Enqueue，
                // 避免从空闲唤醒路径再次 Reset 清空本周期已累计的统计与完成汇总）
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
                        state.Status = PanSourceCollectAutoQueueState.StatusIdle;
                        state.Message = state.Processed > 0
                            ? $"自动补图完成：处理 {state.Processed} 条，回写 {state.Updated} 条，跳过 {state.Skipped} 条，剔除失效图片 {state.FailedImages} 张"
                            : "自动补图队列已清空";
                        state.UpdateTime = DateTime.Now;
                    });
                }
            }
        }

        private async Task RunAsync(string jobId, List<long> ids, int limit, CancellationToken token)
        {
            try
            {
                List<PanSourceCollectEntity> records = ids.Count > 0
                    ? await panSourceCollectService.GetListByIds(ids).ConfigureAwait(false)
                    : await panSourceCollectService.GetImagePendingList(limit).ConfigureAwait(false);
                records = FilterRecords(records);

                ImageProcessResult result = new ImageProcessResult { Total = records.Count };
                PanSourceCollectImageJobManager.Update(jobId, state =>
                {
                    state.Total = records.Count;
                    state.Message = records.Count == 0
                        ? "没有需要补图的记录"
                        : $"开始下载本地图片，共 {records.Count} 条记录";
                });
                if (records.Count == 0)
                {
                    PanSourceCollectImageJobManager.Finish(
                        jobId, PanSourceCollectImageJobManager.StatusSucceeded, "没有需要补图的记录");
                    return;
                }

                await ProcessAsync(records, token, result, current =>
                {
                    PanSourceCollectImageJobManager.Update(jobId, state =>
                    {
                        state.Done = current.Done;
                        state.Updated = current.Updated;
                        state.Skipped = current.Skipped;
                        state.FailedImages = current.FailedImages;
                        state.Message = $"正在下载本地图片：{current.Done}/{state.Total}";
                    });
                }).ConfigureAwait(false);

                PanSourceCollectImageJobManager.Finish(
                    jobId,
                    PanSourceCollectImageJobManager.StatusSucceeded,
                    $"补图完成：处理 {result.Total} 条，回写 {result.Updated} 条，跳过 {result.Skipped} 条，" +
                    $"剔除失效图片 {result.FailedImages} 张");
            }
            catch (OperationCanceledException)
            {
                PanSourceCollectImageJobManager.Finish(
                    jobId, PanSourceCollectImageJobManager.StatusCanceled, "补图已取消，已回写的数据保留");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex);
                PanSourceCollectImageJobManager.Finish(
                    jobId, PanSourceCollectImageJobManager.StatusFailed, "补图失败：" + ex.Message);
            }
        }

        /// <summary>逐条处理记录：本地图齐全则跳过，否则下载并回写两列。</summary>
        private async Task ProcessAsync(
            List<PanSourceCollectEntity> records,
            CancellationToken token,
            ImageProcessResult result,
            Action<ImageProcessResult> onProgress)
        {
            List<Task> tasks = new List<Task>();
            using SemaphoreSlim semaphore = new SemaphoreSlim(RecordConcurrency);
            foreach (PanSourceCollectEntity record in records)
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (IsLocalReady(record))
                        {
                            Interlocked.Increment(ref result.Skipped);
                            return;
                        }

                        (string collectImageUrl, string imageUrl) = await PanSourceCollectImageDownloader
                            .DownloadAsync(record.CollectImageUrl, record.CollectSourceUrl, token)
                            .ConfigureAwait(false);
                        await panSourceCollectService
                            .UpdateImages(record.Id.Value, collectImageUrl, imageUrl)
                            .ConfigureAwait(false);

                        Interlocked.Increment(ref result.Updated);
                        int remoteCount = SiteCollectHtmlUtil.SplitImages(record.CollectImageUrl).Count;
                        int keptCount = SiteCollectHtmlUtil.SplitImages(collectImageUrl).Count;
                        if (remoteCount > keptCount)
                        {
                            Interlocked.Add(ref result.FailedImages, remoteCount - keptCount);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex);
                    }
                    finally
                    {
                        semaphore.Release();
                        Interlocked.Increment(ref result.Done);
                        onProgress?.Invoke(result);
                    }
                }, CancellationToken.None));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>只保留有 Id 且有原图地址的记录。</summary>
        private static List<PanSourceCollectEntity> FilterRecords(IEnumerable<PanSourceCollectEntity> records)
        {
            return (records ?? Enumerable.Empty<PanSourceCollectEntity>())
                .Where(t => t != null && t.Id != null && t.Id.Value > 0 &&
                            !string.IsNullOrWhiteSpace(t.CollectImageUrl))
                .ToList();
        }

        /// <summary>本地图已齐全且文件仍在磁盘上时直接跳过（保证重复触发不重复下载）。</summary>
        private static bool IsLocalReady(PanSourceCollectEntity record)
        {
            List<string> localImages = SiteCollectHtmlUtil.SplitImages(record.ImageUrl);
            if (localImages.Count == 0)
            {
                return false;
            }
            return localImages.All(PanSourceCollectImageDownloader.LocalFileExists);
        }

        /// <summary>任务计数（字段读写用 Interlocked，保证多线程安全）。</summary>
        private sealed class ImageProcessResult
        {
            public int Total;
            public int Done;
            public int Updated;
            public int Skipped;
            public int FailedImages;
        }
    }
}
