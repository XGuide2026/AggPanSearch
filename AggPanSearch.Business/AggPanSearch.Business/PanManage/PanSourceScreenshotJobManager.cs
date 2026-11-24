using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：自动截图任务的内存状态（后台截图 + 前端轮询进度）。
    /// 不做持久化，进程重启后任务丢失，重新触发截图即可。
    /// </summary>
    public class PanSourceScreenshotJobState
    {
        public string JobId { get; set; } = string.Empty;

        /// <summary>running / succeeded / failed / canceled。</summary>
        public string Status { get; set; } = PanSourceScreenshotJobManager.StatusRunning;

        /// <summary>本次任务涉及的资源总数。</summary>
        public int Total { get; set; }

        /// <summary>已处理资源数。</summary>
        public int Done { get; set; }

        /// <summary>截图成功并写入 vod_pic 的资源数。</summary>
        public int Succeeded { get; set; }

        /// <summary>按规则跳过的资源数（非夸克、已有截图）。</summary>
        public int Skipped { get; set; }

        /// <summary>截图失败的资源数。</summary>
        public int Failed { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>本次任务生成的图片站内地址（暂存模式回传用；写库模式为空）。</summary>
        public List<string> Images { get; set; } = new List<string>();

        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime? EndTime { get; set; }

        public PanSourceScreenshotJobState Clone()
        {
            PanSourceScreenshotJobState copy = (PanSourceScreenshotJobState)MemberwiseClone();
            copy.Images = new List<string>(Images);
            return copy;
        }
    }

    /// <summary>
    /// 描 述：自动截图任务注册表（进程内静态单例）。
    /// 截图要跑无头浏览器且外部站点响应慢，全局只允许运行 1 个任务，避免服务器内存与带宽被打满。
    /// </summary>
    public static class PanSourceScreenshotJobManager
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

            public PanSourceScreenshotJobState State = new PanSourceScreenshotJobState();

            public CancellationTokenSource Cts = new CancellationTokenSource();
        }

        /// <summary>创建截图任务并返回 JobId 与取消令牌；已有任务运行时返回空 JobId。</summary>
        public static string Create(int total, out CancellationToken token, out string error)
        {
            token = CancellationToken.None;
            error = string.Empty;
            Cleanup();

            lock (CreateLock)
            {
                if (Jobs.Values.Any(GetIsRunning))
                {
                    error = "已有截图任务在运行，请等待完成后再试";
                    return string.Empty;
                }

                string jobId = Guid.NewGuid().ToString("N");
                JobEntry entry = new JobEntry
                {
                    State = new PanSourceScreenshotJobState
                    {
                        JobId = jobId,
                        Status = StatusRunning,
                        Total = total,
                        Message = "截图任务已启动",
                        StartTime = DateTime.Now,
                    },
                };
                Jobs[jobId] = entry;
                token = entry.Cts.Token;
                return jobId;
            }
        }

        public static PanSourceScreenshotJobState Get(string jobId)
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

        /// <summary>取最近启动且仍在运行的截图任务（页面刷新后恢复进度显示用）。</summary>
        public static PanSourceScreenshotJobState GetLatestRunning()
        {
            PanSourceScreenshotJobState latest = null;
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

        public static bool Update(string jobId, Action<PanSourceScreenshotJobState> update)
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
                entry.State.Message = "正在取消截图…";
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
    /// 描 述：自动截图任务执行器。
    /// 逐条串行调用 <see cref="QuarkShareScreenshotCapturer"/> 截图，图片落盘到 Resource/Collect/年/月/日，
    /// 并把访问路径追加到 pan_source.vod_pic 末尾（保留原有图片，截图作为最后一张）。
    /// 单条失败不中断整批，最终汇总成功 / 跳过 / 失败数量与失败原因。
    /// </summary>
    public sealed class PanSourceScreenshotJobRunner
    {
        /// <summary>单次任务最多处理的资源条数（每条 3~10 秒，避免任务过长）。</summary>
        private const int MaxIds = 50;

        /// <summary>vod_pic 最多保留的图片张数，与编辑页保持一致。</summary>
        private const int MaxImages = 5;

        /// <summary>自动截图文件名前缀，用于识别「已截过图」避免重复追加。</summary>
        private const string ShotFilePrefix = "shot_";

        private readonly PanSourceService panSourceService = new PanSourceService();

        /// <summary>
        /// 启动截图任务：ids 为选中的资源 Id，返回 JobId（失败时返回空并写入 error）。
        /// mode 为截图模式：1=打开文件夹显示文件列表截图，2=打开文件夹显示文件缩略图截图。
        /// persist=true 写库（批量自动截图），persist=false 为暂存模式（资源图片生成弹窗）：
        /// 不写库，图片地址回传到任务状态 Images 由弹窗统一保存。
        /// </summary>
        public static string Start(List<long> ids, int mode, bool persist, out string error)
        {
            error = string.Empty;
            List<long> targetIds = (ids ?? new List<long>())
                .Where(t => t > 0)
                .Distinct()
                .Take(MaxIds)
                .ToList();
            if (targetIds.Count == 0)
            {
                error = "请选择要截图的资源";
                return string.Empty;
            }

            string jobId = PanSourceScreenshotJobManager.Create(targetIds.Count, out CancellationToken token, out error);
            if (jobId.Length == 0)
            {
                return string.Empty;
            }

            PanSourceScreenshotJobRunner runner = new PanSourceScreenshotJobRunner();
            _ = Task.Run(() => runner.RunAsync(jobId, targetIds, mode, persist, token), CancellationToken.None);
            return jobId;
        }

        private async Task RunAsync(string jobId, List<long> ids, int mode, bool persist, CancellationToken token)
        {
            int done = 0;
            int succeeded = 0;
            int skipped = 0;
            int failed = 0;
            List<string> notes = new List<string>();

            try
            {
                List<PanSourceEntity> records = await panSourceService.GetListByIds(ids).ConfigureAwait(false);
                Dictionary<long, PanSourceEntity> recordMap = records
                    .Where(t => t != null && t.Id != null)
                    .GroupBy(t => t.Id.Value)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (long id in ids)
                {
                    token.ThrowIfCancellationRequested();

                    int currentIndex = done + 1;
                    PanSourceScreenshotJobManager.Update(jobId, state =>
                    {
                        state.Message = persist
                            ? $"正在截图：{currentIndex}/{ids.Count}"
                            : $"正在生成图片：{currentIndex}/{ids.Count}";
                    });

                    if (!recordMap.TryGetValue(id, out PanSourceEntity record))
                    {
                        failed++;
                        AddNote(notes, $"Id {id}：记录不存在");
                    }
                    else
                    {
                        string skipReason = GetSkipReason(record, ignoreScreenshotCheck: !persist);
                        if (skipReason.Length > 0)
                        {
                            skipped++;
                            AddNote(notes, $"{ShortTitle(record.Title)}：{skipReason}");
                        }
                        else
                        {
                            (byte[] bytes, string captureError) = await CaptureByModeAsync(mode, record, token)
                                .ConfigureAwait(false);

                            if (bytes == null || bytes.Length == 0)
                            {
                                failed++;
                                AddNote(notes, $"{ShortTitle(record.Title)}：{(captureError.Length > 0 ? captureError : "截图失败")}");
                                LogHelper.Info($"截图失败：Id={id}，原因：{captureError}");
                            }
                            else
                            {
                                string imageUrl = await SaveImageAsync(bytes, token).ConfigureAwait(false);
                                if (imageUrl.Length == 0)
                                {
                                    failed++;
                                    AddNote(notes, $"{ShortTitle(record.Title)}：图片保存失败");
                                }
                                else
                                {
                                    if (persist)
                                    {
                                        string vodPic = AppendImage(record.VodPic, imageUrl);
                                        await panSourceService.UpdateVodPic(id, vodPic).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        string img = imageUrl;
                                        PanSourceScreenshotJobManager.Update(jobId, state =>
                                        {
                                            if (!state.Images.Contains(img, StringComparer.OrdinalIgnoreCase))
                                            {
                                                state.Images.Add(img);
                                            }
                                        });
                                    }
                                    succeeded++;
                                }
                            }
                        }
                    }

                    done++;
                    int doneSnapshot = done;
                    int succeededSnapshot = succeeded;
                    int skippedSnapshot = skipped;
                    int failedSnapshot = failed;
                    PanSourceScreenshotJobManager.Update(jobId, state =>
                    {
                        state.Done = doneSnapshot;
                        state.Succeeded = succeededSnapshot;
                        state.Skipped = skippedSnapshot;
                        state.Failed = failedSnapshot;
                        state.Message = persist
                            ? $"正在截图：{doneSnapshot}/{state.Total}"
                            : $"正在生成图片：{doneSnapshot}/{state.Total}";
                    });
                }

                string message = persist
                    ? $"自动截图完成：处理 {ids.Count} 条，成功 {succeeded} 条，跳过 {skipped} 条，失败 {failed} 条"
                    : $"图片生成完成：生成 {succeeded} 张，跳过 {skipped} 条，失败 {failed} 条（未保存，可在弹窗内预览后保存）";
                if (notes.Count > 0)
                {
                    message += "。" + string.Join("；", notes);
                }
                PanSourceScreenshotJobManager.Finish(jobId, PanSourceScreenshotJobManager.StatusSucceeded, message);
            }
            catch (OperationCanceledException)
            {
                PanSourceScreenshotJobManager.Update(jobId, state =>
                {
                    state.Done = done;
                    state.Succeeded = succeeded;
                    state.Skipped = skipped;
                    state.Failed = failed;
                });
                PanSourceScreenshotJobManager.Finish(
                    jobId,
                    PanSourceScreenshotJobManager.StatusCanceled,
                    $"截图已取消：已处理 {done} 条，成功 {succeeded} 条，已保存的截图保留");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex);
                PanSourceScreenshotJobManager.Finish(
                    jobId, PanSourceScreenshotJobManager.StatusFailed, "截图任务异常：" + ex.Message);
            }
        }

        /// <summary>不需要截图的资源直接跳过，返回跳过原因（空字符串表示需要处理）。</summary>
        /// <param name="ignoreScreenshotCheck">暂存模式下允许对已有截图的资源重新生成（只影响暂存列表，不写库）。</param>
        private static string GetSkipReason(PanSourceEntity record, bool ignoreScreenshotCheck)
        {
            if (string.IsNullOrWhiteSpace(record.Url))
            {
                return "资源地址为空";
            }
            // IsType：0 夸克 / 1 阿里 / 2 百度 / 3 UC / 4 迅雷，null 视为夸克
            if (record.IsType != null && record.IsType != 0)
            {
                return "仅支持夸克资源";
            }
            if (!ignoreScreenshotCheck && HasScreenshot(record.VodPic))
            {
                return "已有自动截图";
            }
            return string.Empty;
        }

        /// <summary>按截图模式选择执行器。</summary>
        /// <remarks>mode=1（默认）：打开文件夹显示文件列表截图；
        /// mode=2：打开文件夹显示文件缩略图截图（先切换视图再截图）。</remarks>
        private static async Task<(byte[] Bytes, string Error)> CaptureByModeAsync(
            int mode, PanSourceEntity record, CancellationToken token)
        {
            switch (mode)
            {
                case 2:
                    return await QuarkShareScreenshotCapturer.CaptureAsync(
                        record.Url, record.Code, token, switchToThumbnail: true).ConfigureAwait(false);
                default:
                    return await QuarkShareScreenshotCapturer.CaptureAsync(record.Url, record.Code, token)
                        .ConfigureAwait(false);
            }
        }

        /// <summary>图片落盘到 Resource/Collect/年/月/日，返回站内访问地址；失败返回空字符串。</summary>
        private static async Task<string> SaveImageAsync(byte[] bytes, CancellationToken token)
        {
            try
            {
                string relativeDirectory = PanSourceCollectImageDownloader.BuildRelativeDirectory();
                string absoluteDirectory = Path.Combine(GlobalContext.HostingEnvironment.ContentRootPath, relativeDirectory);
                if (!Directory.Exists(absoluteDirectory))
                {
                    Directory.CreateDirectory(absoluteDirectory);
                }
                string fileName = ShotFilePrefix + SecurityHelper.GetGuid(true) + ".png";
                await File.WriteAllBytesAsync(Path.Combine(absoluteDirectory, fileName), bytes, token).ConfigureAwait(false);
                return "/" + FileHelper.ConvertDirectoryToHttp(Path.Combine(relativeDirectory, fileName));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogHelper.Info($"自动截图保存失败：{ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>把截图地址追加到 vod_pic 末尾，超过上限时移除非首图的最早一张。</summary>
        private static string AppendImage(string vodPic, string imageUrl)
        {
            List<string> images = SplitImages(vodPic);
            if (!images.Contains(imageUrl, StringComparer.OrdinalIgnoreCase))
            {
                images.Add(imageUrl);
            }
            // 首图是列表封面，追加的截图永远排在最后，超出上限时从第 2 张开始移除
            while (images.Count > MaxImages && images.Count > 1)
            {
                images.RemoveAt(1);
            }
            return string.Join(";", images);
        }

        private static bool HasScreenshot(string vodPic)
        {
            return SplitImages(vodPic).Any(t =>
                t.IndexOf("/" + ShotFilePrefix, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<string> SplitImages(string vodPic)
        {
            return (vodPic ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        /// <summary>汇总提示里的资源名截断，避免日志与弹窗过长。</summary>
        private static string ShortTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "未命名资源";
            }
            string text = title.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length > 16 ? text.Substring(0, 16) + "…" : text;
        }

        /// <summary>最多保留 6 条明细，避免 Message 过长。</summary>
        private static void AddNote(List<string> notes, string note)
        {
            if (notes.Count < 6)
            {
                notes.Add(note);
            }
        }
    }
}
