using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AggPanSearch.Business.PanManage.Quark
{
    /// <summary>
    /// 描 述：单条资源的转存结果（用于前端逐条展示失败原因）。
    /// </summary>
    public sealed class QuarkPanTransferItem
    {
        public const string StatusPending = "pending";
        public const string StatusSucceeded = "succeeded";
        public const string StatusSkipped = "skipped";
        public const string StatusFailed = "failed";

        /// <summary>采集资源 Id（pan_source_collect.Id）。</summary>
        public long Id { get; set; }

        /// <summary>资源名称（前端展示用）。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>原始分享链接。</summary>
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>pending / succeeded / skipped / failed。</summary>
        public string Status { get; set; } = StatusPending;

        /// <summary>结果说明（失败原因、跳过原因或新分享链接）。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>转存后生成的新分享链接（成功时有值）。</summary>
        public string ShareUrl { get; set; } = string.Empty;

        public QuarkPanTransferItem Clone()
        {
            return (QuarkPanTransferItem)MemberwiseClone();
        }
    }

    /// <summary>
    /// 描 述：转存任务的内存状态（后台任务 + 前端轮询进度），不做持久化。
    /// </summary>
    public sealed class QuarkPanTransferState
    {
        public string JobId { get; set; } = string.Empty;

        /// <summary>running / succeeded / failed / canceled。</summary>
        public string Status { get; set; } = QuarkPanTransferJobManager.StatusRunning;

        public int Total { get; set; }

        public int Done { get; set; }

        public int Succeeded { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime? EndTime { get; set; }

        /// <summary>逐条结果明细。</summary>
        public List<QuarkPanTransferItem> Items { get; set; } = new List<QuarkPanTransferItem>();

        public QuarkPanTransferState Clone()
        {
            QuarkPanTransferState copy = (QuarkPanTransferState)MemberwiseClone();
            copy.Items = Items == null
                ? new List<QuarkPanTransferItem>()
                : Items.Select(t => t.Clone()).ToList();
            return copy;
        }
    }

    /// <summary>
    /// 描 述：转存任务注册表（进程内静态单例）。
    /// 同一账号同一时间只允许一个转存任务（避免并发触发风控），已完成任务保留 2 小时后清理。
    /// </summary>
    public static class QuarkPanTransferJobManager
    {
        public const string StatusRunning = "running";
        public const string StatusSucceeded = "succeeded";
        public const string StatusFailed = "failed";
        public const string StatusCanceled = "canceled";

        private const int MaxRunningJobs = 1;
        private const int MaxJobCount = 20;

        private static readonly TimeSpan Retention = TimeSpan.FromHours(2);
        private static readonly object CreateLock = new object();

        private static readonly ConcurrentDictionary<string, JobEntry> Jobs =
            new ConcurrentDictionary<string, JobEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class JobEntry
        {
            public readonly object SyncRoot = new object();

            public QuarkPanTransferState State = new QuarkPanTransferState();

            public CancellationTokenSource Cts = new CancellationTokenSource();
        }

        /// <summary>
        /// 创建转存任务并返回 JobId 与取消令牌；已有任务在运行时返回空 JobId 并给出原因。
        /// </summary>
        public static string Create(List<QuarkPanTransferItem> items, out CancellationToken token, out string error)
        {
            token = CancellationToken.None;
            error = string.Empty;
            if (items == null || items.Count == 0)
            {
                error = "请选择要转存的资源";
                return string.Empty;
            }

            Cleanup();

            lock (CreateLock)
            {
                if (Jobs.Values.Any(t => GetStatus(t) == StatusRunning))
                {
                    error = "已有转存任务在运行，请等待完成后再试";
                    return string.Empty;
                }

                string jobId = Guid.NewGuid().ToString("N");
                JobEntry entry = new JobEntry
                {
                    State = new QuarkPanTransferState
                    {
                        JobId = jobId,
                        Status = StatusRunning,
                        Total = items.Count,
                        Message = "转存任务已启动",
                        StartTime = DateTime.Now,
                        Items = items.Select(t => t.Clone()).ToList(),
                    },
                };
                Jobs[jobId] = entry;
                token = entry.Cts.Token;
                return jobId;
            }
        }

        public static QuarkPanTransferState Get(string jobId)
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

        /// <summary>取最近启动且仍在运行的任务（页面刷新后恢复进度显示用）。</summary>
        public static QuarkPanTransferState GetLatestRunning()
        {
            QuarkPanTransferState latest = null;
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

        /// <summary>更新任务整体状态（仅运行中的任务生效）。</summary>
        public static bool Update(string jobId, Action<QuarkPanTransferState> update)
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

        /// <summary>
        /// 回写单条资源的处理结果并累加统计（成功/跳过/失败）。
        /// 找不到对应条目时忽略，不影响主流程。
        /// </summary>
        public static bool UpdateItem(string jobId, long id, string status, string message, string shareUrl)
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
                QuarkPanTransferItem item = entry.State.Items.FirstOrDefault(t => t.Id == id);
                if (item == null)
                {
                    return false;
                }
                if (item.Status != QuarkPanTransferItem.StatusPending)
                {
                    return false;
                }

                item.Status = status;
                item.Message = message ?? string.Empty;
                item.ShareUrl = shareUrl ?? string.Empty;
                entry.State.Done++;
                if (status == QuarkPanTransferItem.StatusSucceeded)
                {
                    entry.State.Succeeded++;
                }
                else if (status == QuarkPanTransferItem.StatusSkipped)
                {
                    entry.State.Skipped++;
                }
                else if (status == QuarkPanTransferItem.StatusFailed)
                {
                    entry.State.Failed++;
                }
                entry.State.Message = string.Format("已处理 {0}/{1}", entry.State.Done, entry.State.Total);
            }
            return true;
        }

        /// <summary>结束任务并写入最终状态。</summary>
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

        /// <summary>请求取消任务：仅对运行中的任务生效。</summary>
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
                entry.State.Message = "正在取消转存…";
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

        private static string GetStatus(JobEntry entry)
        {
            lock (entry.SyncRoot)
            {
                return entry.State.Status;
            }
        }

        /// <summary>清理超过保留期的已完成任务，并在总数超限时移除最旧的任务。</summary>
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
}
