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
    /// 描 述：资源图片下载队列的实时进度状态（进程内静态，供前端轮询展示）。
    /// 采集入库后由 <see cref="PanSourceCollectImageJobRunner.Enqueue"/> 入队，
    /// 队列工作线程逐批处理后把统计累加到这里，页面通过接口读取并展示进度。
    /// 各计数单位均为「图片张数」，其中 Total 由采集完成后先按已入库数据精确算出。
    /// </summary>
    public sealed class PanSourceCollectAutoQueueState
    {
        public const string StatusIdle = "idle";
        public const string StatusRunning = "running";

        /// <summary>idle（队列空闲）/ running（队列处理中）。</summary>
        public string Status { get; set; } = StatusIdle;

        /// <summary>当前处理周期应下载的图片总张数（资源采集完成后先精确算出，Enqueue 时累加）。</summary>
        public int Total { get; set; }

        /// <summary>已处理的图片张数（含本地已齐全跳过的与成功入库的），= Skipped + Updated + FailedImages。</summary>
        public int Processed { get; set; }

        /// <summary>成功下载并入库的图片张数。</summary>
        public int Updated { get; set; }

        /// <summary>本地图已齐全、跳过下载的图片张数。</summary>
        public int Skipped { get; set; }

        /// <summary>失效被剔除的图片张数。</summary>
        public int FailedImages { get; set; }

        /// <summary>剩余待下载的图片张数（Total - Processed）。</summary>
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
    /// 描 述：资源图片下载执行器（资源图片下载队列）。
    /// 把记录中 collect_image_url 的远端图片下载到本机，并只回写 collect_image_url / image_url 两列；
    /// 下载失败的图片会从 collect_image_url 中剔除，因此任务结束后原图字段可能变短。
    /// 采集入库后由 <see cref="Enqueue"/> 自动入队，队列工作线程逐批处理，不阻塞采集主流程。
    /// </summary>
    public sealed class PanSourceCollectImageJobRunner
    {
        /// <summary>记录之间的并发数，记录内图片保持顺序下载以维持索引对应。</summary>
        private const int RecordConcurrency = 3;

        /// <summary>资源图片下载队列单次批量。</summary>
        private const int QueueBatchSize = 100;

        private static readonly ConcurrentQueue<long> PendingIds = new ConcurrentQueue<long>();
        private static int queueWorkerRunning;

        /// <summary>资源图片下载队列状态（锁保护，前端轮询读取）。</summary>
        private static readonly object AutoQueueStateLock = new object();
        private static PanSourceCollectAutoQueueState autoQueueState = new PanSourceCollectAutoQueueState();

        /// <summary>当前周期已处理记录 Id 的累积（锁保护）：图片下载队列全部完成后一次性投递给资源文件分析队列，保证「先下载、后分析」的严格顺序。</summary>
        private static readonly List<long> PendingAnalyzeIds = new List<long>();

        private readonly PanSourceCollectService panSourceCollectService = new PanSourceCollectService();

        /// <summary>获取资源图片下载队列的实时进度状态（返回副本，调用方可自由读取）。</summary>
        public static PanSourceCollectAutoQueueState GetAutoQueueState()
        {
            lock (AutoQueueStateLock)
            {
                return autoQueueState.Clone();
            }
        }

        /// <summary>采集入库后把新增记录投递到资源图片下载队列，并登记本批应下载的图片总张数（不等待下载）。</summary>
        public static void Enqueue(IEnumerable<long>? ids, int totalImages = 0)
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
            if (added > 0 && Interlocked.CompareExchange(ref queueWorkerRunning, 1, 0) == 0)
            {
                // 从空闲唤醒：重置本周期的累计统计
                ResetAutoQueueState();
                _ = Task.Run(DrainQueueAsync, CancellationToken.None);
            }
            // 登记图片总张数：Total 为精确值，由采集完成后先按已入库数据算出，不再按记录条数近似；
            // 若队列在本周期运行中又入队新一批，则继续累加
            if (totalImages > 0)
            {
                UpdateAutoQueueState(state =>
                {
                    state.Total += totalImages;
                    state.Pending = Math.Max(0, state.Total - state.Processed);
                });
            }
        }

        /// <summary>重置资源图片下载队列统计并标记为运行中（队列从空闲唤醒时调用）。</summary>
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
                autoQueueState.Pending = 0;
                autoQueueState.Message = "资源图片下载已启动，正在下载本地图片…";
                autoQueueState.UpdateTime = DateTime.Now;
                // 从空闲唤醒开始新周期：清空上一周期累积的待分析 Id
                PendingAnalyzeIds.Clear();
            }
        }

        /// <summary>在锁内更新资源图片下载队列状态，并刷新更新时间。</summary>
        private static void UpdateAutoQueueState(Action<PanSourceCollectAutoQueueState> update)
        {
            lock (AutoQueueStateLock)
            {
                update(autoQueueState);
                autoQueueState.UpdateTime = DateTime.Now;
            }
        }

        /// <summary>资源图片下载队列工作线程：循环取空队列后退出。</summary>
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
                    // 先累积本批 Id：图片下载队列全部完成后再一次性投递资源文件分析队列，保证顺序
                    lock (AutoQueueStateLock)
                    {
                        foreach (long id in batch)
                        {
                            if (id > 0)
                            {
                                PendingAnalyzeIds.Add(id);
                            }
                        }
                    }
                    try
                    {
                        List<PanSourceCollectEntity> records =
                            await runner.panSourceCollectService.GetListByIds(batch).ConfigureAwait(false);
                        records = FilterRecords(records);
                        if (records.Count > 0)
                        {
                            int batchImages = records.Sum(t => CountImages(t.CollectImageUrl));
                            UpdateAutoQueueState(state =>
                            {
                                state.Message = $"正在下载本地图片：本批 {batchImages} 张…";
                                state.UpdateTime = DateTime.Now;
                            });
                        }
                        ImageProcessResult result = new ImageProcessResult();
                        if (records.Count > 0)
                        {
                            // 逐张/逐条实时上报进度：ProcessAsync 每完成一条记录都会触发回调，
                            // 这里以「与上次上报的差值」累加进队列状态，前端轮询即可看到进度渐进推进，
                            // 避免整批（最多 100 条、数百张图）处理完才一次性跳变。
                            int lastDone = 0, lastUpdated = 0, lastSkipped = 0, lastFailed = 0;
                            object progressLock = new object();
                            await runner.ProcessAsync(
                                records,
                                CancellationToken.None,
                                result,
                                progress =>
                                {
                                    int done, updated, skipped, failed;
                                    lock (progressLock)
                                    {
                                        // 并发回调下串行读取基准值：result 字段由 Interlocked 单调递增，
                                        // 用上次快照求差值，避免并发调用时统计重复累加。
                                        done = progress.Done - lastDone;
                                        updated = progress.Updated - lastUpdated;
                                        skipped = progress.Skipped - lastSkipped;
                                        failed = progress.FailedImages - lastFailed;
                                        lastDone = progress.Done;
                                        lastUpdated = progress.Updated;
                                        lastSkipped = progress.Skipped;
                                        lastFailed = progress.FailedImages;
                                    }
                                    if (done == 0 && updated == 0 && skipped == 0 && failed == 0)
                                    {
                                        return;
                                    }
                                    UpdateAutoQueueState(state =>
                                    {
                                        state.Processed += done;
                                        state.Updated += updated;
                                        state.Skipped += skipped;
                                        state.FailedImages += failed;
                                        state.Pending = Math.Max(0, state.Total - state.Processed);
                                        // Message 保持批开始时文案即可，前端自行拼装"已处理/入库/剩余"明细。
                                    });
                                }).ConfigureAwait(false);
                        }
                        UpdateAutoQueueState(state =>
                        {
                            // 张数统计已在逐张/逐条上报中实时累加，这里只补充批汇总文案，避免与实时上报重复累加。
                            state.Message = records.Count > 0
                                ? $"本批处理完成：入库 {result.Updated} 张，跳过 {result.Skipped} 张，剔除失效图片 {result.FailedImages} 张"
                                : "本批没有需要下载资源图片的记录";
                        });
                        // 注意：不再在本批结束后入队资源文件分析。所有批次处理完后，
                        // 在 finally 的空闲分支统一把整周期 Id 一次性投递给资源文件分析队列，
                        // 从而保证「图片下载全部完成 → 文件分析开始」的严格先后顺序。
                    }
                    catch (Exception ex)
                    {
                        // 资源图片下载失败不影响采集与后续队列
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
                    // 图片下载队列真正空闲：把本周期累积的全部记录 Id 一次性投递给资源文件分析队列。
                    // 先在锁内取出并清空，再在锁外 Enqueue（分析队列持有独立锁，避免锁内跨 Runner 交叉竞争）。
                    List<long> analyzeIds;
                    lock (AutoQueueStateLock)
                    {
                        analyzeIds = new List<long>(PendingAnalyzeIds);
                        PendingAnalyzeIds.Clear();
                    }
                    if (analyzeIds.Count > 0)
                    {
                        PanSourceCollectInfoJobRunner.Enqueue(analyzeIds);
                    }
                    UpdateAutoQueueState(state =>
                    {
                        state.Status = PanSourceCollectAutoQueueState.StatusIdle;
                        state.Message = state.Processed > 0
                            ? $"资源图片下载完成：共 {state.Total} 张，入库 {state.Updated} 张，跳过 {state.Skipped} 张，剔除失效图片 {state.FailedImages} 张"
                            : "资源图片下载队列已清空";
                        state.UpdateTime = DateTime.Now;
                    });
                }
            }
        }

        /// <summary>逐条处理记录：本地图齐全则跳过，否则下载并回写两列（统计单位：图片张数）。</summary>
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
                    int remoteCount = 0;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        remoteCount = CountImages(record.CollectImageUrl);
                        if (IsLocalReady(record))
                        {
                            // 本地图已齐全：该条的图片张数整体跳过
                            Interlocked.Add(ref result.Skipped, remoteCount);
                            return;
                        }

                        (string collectImageUrl, string imageUrl) = await PanSourceCollectImageDownloader
                            .DownloadAsync(record.CollectImageUrl, record.CollectSourceUrl, token)
                            .ConfigureAwait(false);
                        await panSourceCollectService
                            .UpdateImages(record.Id.Value, collectImageUrl, imageUrl)
                            .ConfigureAwait(false);

                        int keptCount = SiteCollectHtmlUtil.SplitImages(collectImageUrl).Count;
                        Interlocked.Add(ref result.Updated, keptCount);
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
                        // 该条的图片张数整体视为已处理（含成功、跳过与剔除）
                        Interlocked.Add(ref result.Done, remoteCount);
                        onProgress?.Invoke(result);
                    }
                }, CancellationToken.None));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>图片张数统计口径，与下载端完全一致：过滤无效地址、最多取 MaxImageCount 张。</summary>
        private static int CountImages(string collectImageUrl)
        {
            return SiteCollectHtmlUtil.SplitImages(collectImageUrl)
                .Where(image => SiteCollectHtmlUtil.TryCreateHttpUri(image, out _))
                .Take(SiteCollectHtmlUtil.MaxImageCount)
                .Count();
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