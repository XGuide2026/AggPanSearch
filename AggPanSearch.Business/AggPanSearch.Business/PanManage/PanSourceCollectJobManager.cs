using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集任务的内存状态（后台任务 + 前端轮询进度）。
    /// 不做持久化，进程重启后任务丢失，重新采集即可。
    /// </summary>
    public class PanSourceCollectJobState
    {
        public string JobId { get; set; } = string.Empty;

        /// <summary>采集模式：0 详情页采集 / 1 单个列表页采集 / 2 分页列表采集。</summary>
        public int Mode { get; set; }

        public string SiteName { get; set; } = string.Empty;

        public string CollectSiteUrl { get; set; } = string.Empty;

        public string CollectSource { get; set; } = string.Empty;

        /// <summary>running / succeeded / failed / canceled。</summary>
        public string Status { get; set; } = PanSourceCollectJobManager.StatusRunning;

        /// <summary>已处理的列表页数量。</summary>
        public int PagesDone { get; set; }

        /// <summary>预计列表页总数，0 表示未知。</summary>
        public int PagesTotal { get; set; }

        public int Found { get; set; }

        public int Inserted { get; set; }

        public int Duplicated { get; set; }

        public int Skipped { get; set; }

        /// <summary>是否因页数或条数上限提前结束。</summary>
        public bool LimitReached { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime? EndTime { get; set; }

        public PanSourceCollectJobState Clone()
        {
            return (PanSourceCollectJobState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 描 述：采集任务注册表（进程内静态单例）。
    /// 负责创建、查询、取消任务，并清理过期任务、限制并发任务数。
    /// </summary>
    public static class PanSourceCollectJobManager
    {
        public const string StatusRunning = "running";
        public const string StatusSucceeded = "succeeded";
        public const string StatusFailed = "failed";
        public const string StatusCanceled = "canceled";

        private const int MaxRunningJobs = 3;
        private const int MaxJobCount = 50;

        private static readonly TimeSpan Retention = TimeSpan.FromHours(2);
        private static readonly object CreateLock = new object();

        private static readonly ConcurrentDictionary<string, JobEntry> Jobs =
            new ConcurrentDictionary<string, JobEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class JobEntry
        {
            public readonly object SyncRoot = new object();

            public PanSourceCollectJobState State = new PanSourceCollectJobState();

            public CancellationTokenSource Cts = new CancellationTokenSource();
        }

        /// <summary>
        /// 创建任务并返回 JobId 与取消令牌；并发任务过多时返回空 JobId 并给出原因。
        /// </summary>
        public static string Create(int mode, string url, string source, string siteName, out CancellationToken token, out string error)
        {
            token = CancellationToken.None;
            error = string.Empty;
            Cleanup();

            lock (CreateLock)
            {
                if (Jobs.Values.Count(t => GetStatus(t) == StatusRunning) >= MaxRunningJobs)
                {
                    error = "已有采集任务在运行，请等待完成后再试";
                    return string.Empty;
                }

                string jobId = Guid.NewGuid().ToString("N");
                JobEntry entry = new JobEntry
                {
                    State = new PanSourceCollectJobState
                    {
                        JobId = jobId,
                        Mode = mode,
                        SiteName = siteName ?? string.Empty,
                        CollectSiteUrl = url,
                        CollectSource = source,
                        Status = StatusRunning,
                        Message = "采集任务已启动",
                        StartTime = DateTime.Now,
                    },
                };
                Jobs[jobId] = entry;
                token = entry.Cts.Token;
                return jobId;
            }
        }

        public static PanSourceCollectJobState Get(string jobId)
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
        public static PanSourceCollectJobState GetLatestRunning()
        {
            PanSourceCollectJobState latest = null;
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

        /// <summary>更新任务状态（调用方在回调内直接修改字段）。</summary>
        public static bool Update(string jobId, Action<PanSourceCollectJobState> update)
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

        /// <summary>结束任务并写入最终状态。</summary>
        public static void Finish(string jobId, string status, string message, bool limitReached)
        {
            if (string.IsNullOrWhiteSpace(jobId) || !Jobs.TryGetValue(jobId, out JobEntry entry))
            {
                return;
            }
            lock (entry.SyncRoot)
            {
                entry.State.Status = status;
                entry.State.Message = message ?? string.Empty;
                entry.State.LimitReached = limitReached;
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
                entry.State.Message = "正在取消采集…";
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
