using System;
using System.Data.Common;
using System.Globalization;
using System.Threading.Tasks;
using AggPanSearch.Data;
using AggPanSearch.Data.Repository;
using AggPanSearch.Util;

namespace AggPanSearch.Service.PanManage
{
    /// <summary>
    /// 搜索统计快照（不可变值对象），供前台首页"今日搜索 / 总搜索量 / 搜索耗时"读取。
    /// </summary>
    public sealed class PanSearchStatsSnapshot
    {
        public PanSearchStatsSnapshot(DateTime statDate, int today, int total, int avgCostMs, DateTime loadedAt)
        {
            StatDate = statDate;
            Today = today;
            Total = total;
            AvgCostMs = avgCostMs;
            LoadedAt = loadedAt;
        }

        /// <summary>统计归属日期（用于跨天判断，避免把昨天的"今日搜索"当成今天）</summary>
        public DateTime StatDate { get; }

        /// <summary>今日搜索量</summary>
        public int Today { get; }

        /// <summary>总搜索量</summary>
        public int Total { get; }

        /// <summary>平均搜索耗时（毫秒，取自 pan_search_log.cost_ms 的平均值，含缓存命中）</summary>
        public int AvgCostMs { get; }

        /// <summary>平均搜索耗时格式化文本，如 0.86s；无数据时为空串</summary>
        public string AvgCostFormatted => PanSearchStatsService.FormatCost(AvgCostMs);

        /// <summary>快照生成时间</summary>
        public DateTime LoadedAt { get; }
    }

    /// <summary>
    /// 描 述：搜索统计服务（pan_search_log 的今日/总量/平均耗时聚合）
    ///
    /// 性能设计（首页每次访问都要读这些数字，不能每次都 COUNT/AVG 全表）：
    /// 1) 进程内内存快照：读操作基本是内存读，只有过期时才触发一次数据库查询；
    /// 2) 分级 TTL：今日搜索 20s（变化快、但只扫当天的时间索引区间，代价小），
    ///    总搜索量 5min（全表 COUNT 代价大、前端展示 K/W 级别，5 分钟误差无感知），
    ///    平均搜索耗时 5min（SUM/COUNT 要扫全表，仅作兜底校准，日常靠内存增量维护）；
    /// 3) 单飞（single-flight）：并发请求共享同一个刷新任务，缓存过期瞬间不会打出一堆 COUNT；
    /// 4) 陈旧优先（stale-while-revalidate）：过期但仍有旧值时立即返回旧值并后台刷新，请求不被阻塞；
    /// 5) 写入即累加：每次成功写入搜索日志后本地更新内存值（今日/总量 +1，平均耗时累积 sum/count），
    ///    数据近似实时且不产生额外查询——平均耗时不再"写入即重算 AVG"，搜索再频繁也不会每次扫全表；
    /// 6) 跨天自动重置：今日值带日期标记，日期变化后强制重新查询，不会串天。
    /// </summary>
    public class PanSearchStatsService : RepositoryFactory
    {
        private const string SqlToday =
            "SELECT COUNT(1) FROM pan_search_log WHERE CreateTime >= @StartTime AND CreateTime < @EndTime";

        private const string SqlTotal = "SELECT COUNT(1) FROM pan_search_log";

        /// <summary>
        /// 平均搜索耗时的分子：pan_search_log.CostMs 求和。
        /// 命中缓存（IsCache=1）的记录同样计入，即"搜索耗时"= 前端实际等待的搜索响应时间（含缓存命中）；
        /// 仅排除 CostMs 为 NULL/0 的无效记录（历史脏数据）。
        /// 拆成 SUM + COUNT 而不是 AVG，是为了加载后能在内存里持续增量维护平均值，
        /// 避免每次搜索写入后都重新扫一遍全表。
        /// </summary>
        private const string SqlAvgSum =
            "SELECT SUM(CostMs) FROM pan_search_log WHERE CostMs > 0";

        /// <summary>平均搜索耗时的分母（CostMs > 0 的有效样本数）</summary>
        private const string SqlAvgCount =
            "SELECT COUNT(1) FROM pan_search_log WHERE CostMs > 0";

        /// <summary>今日搜索量缓存时长（秒）</summary>
        private static readonly TimeSpan TodayTtl = TimeSpan.FromSeconds(20);

        /// <summary>总搜索量缓存时长</summary>
        private static readonly TimeSpan TotalTtl = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 平均搜索耗时缓存时长（仅作兜底校准：日常由 OnSearchRecorded 在内存里增量累加 sum/count，
        /// 这里的定时重算只用于修正外部直接改库、多实例部署等造成的漂移）
        /// </summary>
        private static readonly TimeSpan AvgCostTtl = TimeSpan.FromMinutes(5);

        /// <summary>查询失败后的退避时长，避免数据库异常时每个请求都去重试</summary>
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);

        private static readonly CountSlot TodaySlot = new CountSlot();
        private static readonly CountSlot TotalSlot = new CountSlot();
        private static readonly CountSlot AvgCostSlot = new CountSlot();

        #region 对外方法

        /// <summary>
        /// 获取统计（异步）。绝大多数调用命中内存，过期时返回旧值并在后台刷新。
        /// </summary>
        public async Task<PanSearchStatsSnapshot> GetStatsAsync()
        {
            DateTime now = DateTime.Now;
            DateTime today = now.Date;

            // 今日搜索：同一天内过期 -> 先返回旧值并后台刷新；跨天 / 首次 -> 必须等待刷新
            bool todayFresh = IsFresh(TodaySlot, now, true);
            if (!todayFresh)
            {
                bool sameDay = HasValueOf(TodaySlot, today);
                Task<int> task = RefreshAsync(TodaySlot, today, TodayTtl, SqlToday, BuildTodayParameters(today));
                if (!sameDay)
                {
                    await task.ConfigureAwait(false);
                }
            }

            // 总搜索量：有过值就陈旧优先，从未成功过才等待
            bool totalFresh = IsFresh(TotalSlot, now, false);
            if (!totalFresh)
            {
                bool hasOld = HasValue(TotalSlot);
                Task<int> task = RefreshAsync(TotalSlot, today, TotalTtl, SqlTotal, null);
                if (!hasOld)
                {
                    await task.ConfigureAwait(false);
                }
            }

            // 平均搜索耗时：SUM/COUNT 要扫全表，只做兜底校准；
            // 新写入的耗时已由 OnSearchRecorded 增量累加进内存，读取时不需要额外重算
            bool avgFresh = IsFresh(AvgCostSlot, now, false);
            if (!avgFresh)
            {
                bool hasOld = HasValue(AvgCostSlot);
                Task<int> task = RefreshAvgCostAsync(AvgCostSlot, today, AvgCostTtl);
                if (!hasOld)
                {
                    await task.ConfigureAwait(false);
                }
            }

            return BuildSnapshot(today);
        }

        /// <summary>
        /// 获取统计（同步）。供 Razor 首屏直接渲染使用：
        /// 有缓存时零等待；确实要查库时最多等 300ms，超时则返回当前内存值不阻塞页面。
        /// </summary>
        public PanSearchStatsSnapshot GetStats()
        {
            DateTime today = DateTime.Now.Date;
            // 快速路径：三者都新鲜时直接读内存
            if (IsFresh(TodaySlot, DateTime.Now, true) && IsFresh(TotalSlot, DateTime.Now, false)
                && IsFresh(AvgCostSlot, DateTime.Now, false))
            {
                return BuildSnapshot(today);
            }

            Task<PanSearchStatsSnapshot> task = GetStatsAsync();
            try
            {
                task.Wait(TimeSpan.FromMilliseconds(300));
            }
            catch
            {
                // 统计失败不影响页面渲染
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task.Result;
            }
            return BuildSnapshot(today);
        }

        /// <summary>今日搜索量</summary>
        public async Task<int> GetTodayCountAsync()
        {
            return (await GetStatsAsync().ConfigureAwait(false)).Today;
        }

        /// <summary>总搜索量</summary>
        public async Task<int> GetTotalCountAsync()
        {
            return (await GetStatsAsync().ConfigureAwait(false)).Total;
        }

        /// <summary>平均搜索耗时（毫秒），取自 pan_search_log.cost_ms 的平均值</summary>
        public async Task<int> GetAvgCostAsync()
        {
            return (await GetStatsAsync().ConfigureAwait(false)).AvgCostMs;
        }

        /// <summary>
        /// 成功写入一条搜索日志后调用：在内存里直接累加，使前台数字近似实时，
        /// 同时免去"写完立刻 COUNT 一次"的数据库开销。
        /// </summary>
        /// <param name="costMs">本次搜索耗时（毫秒）；大于 0 时在内存里累加 sum/count，平均值立即生效</param>
        public void OnSearchRecorded(int costMs = 0)
        {
            DateTime today = DateTime.Now.Date;

            lock (TodaySlot)
            {
                // 跨天时不加，交给下一次刷新重置为当天真实值
                if (TodaySlot.HasValue && TodaySlot.Key == today)
                {
                    TodaySlot.Value++;
                }
            }
            lock (TotalSlot)
            {
                if (TotalSlot.HasValue)
                {
                    TotalSlot.Value++;
                }
            }
            // 平均耗时：内存里累加 sum/count 后重算平均值，搜索完成后立刻可见，
            // 且完全不产生额外数据库查询（不重跑 AVG，不扫全表）
            if (costMs > 0)
            {
                lock (AvgCostSlot)
                {
                    // Count > 0 才说明有可用的 sum/count 基线（首次加载失败时 Count 为 0，不能从 0 开始累加）
                    if (AvgCostSlot.HasValue && AvgCostSlot.Count > 0)
                    {
                        AvgCostSlot.Sum += costMs;
                        AvgCostSlot.Count++;
                        AvgCostSlot.Value = ToAvg(AvgCostSlot.Sum, AvgCostSlot.Count);
                    }
                    else
                    {
                        // 还没有基线（进程刚启动且尚未查库）：交给下一次读取去加载
                        AvgCostSlot.ExpireAt = DateTime.MinValue;
                    }
                }
            }
        }

        /// <summary>失效缓存（清空/批量删除日志后调用）</summary>
        public void Invalidate()
        {
            lock (TodaySlot)
            {
                TodaySlot.HasValue = false;
                TodaySlot.Value = 0;
                TodaySlot.Key = DateTime.MinValue;
                TodaySlot.ExpireAt = DateTime.MinValue;
            }
            lock (TotalSlot)
            {
                TotalSlot.HasValue = false;
                TotalSlot.Value = 0;
                TotalSlot.ExpireAt = DateTime.MinValue;
            }
            lock (AvgCostSlot)
            {
                AvgCostSlot.HasValue = false;
                AvgCostSlot.Value = 0;
                AvgCostSlot.Sum = 0;
                AvgCostSlot.Count = 0;
                AvgCostSlot.ExpireAt = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 搜索量格式化：1万以上用 W，1千以上用 K（保留 1 位小数，向下取整）
        /// </summary>
        public static string FormatCount(int value)
        {
            if (value >= 100000000)
            {
                return (Math.Floor(value / 10000000.0) / 10.0).ToString("0.#") + "亿";
            }
            if (value >= 10000)
            {
                return (Math.Floor(value / 1000.0) / 10.0).ToString("0.#") + "W";
            }
            if (value >= 1000)
            {
                return (Math.Floor(value / 100.0) / 10.0).ToString("0.#") + "K";
            }
            return value.ToString();
        }

        /// <summary>
        /// 平均搜索耗时格式化：毫秒 -> "0.86s"（保留 2 位小数）。
        /// 无有效数据（0 或负数）时返回空串，由调用方保留原有默认展示值。
        /// </summary>
        public static string FormatCost(int avgCostMs)
        {
            if (avgCostMs <= 0)
            {
                return "";
            }
            return (avgCostMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";
        }

        #endregion

        #region 内部实现

        /// <summary>带 TTL 的计数槽位，静态字段共享，进程内唯一</summary>
        private sealed class CountSlot
        {
            public int Value;
            /// <summary>值归属日期（仅今日计数使用）</summary>
            public DateTime Key = DateTime.MinValue;
            public DateTime ExpireAt = DateTime.MinValue;
            public bool HasValue;
            /// <summary>进行中的刷新任务（单飞，避免缓存击穿）</summary>
            public Task<int> Inflight;
            /// <summary>耗时总和（仅平均耗时槽位使用，cost_ms &gt; 0 的有效样本）</summary>
            public long Sum;
            /// <summary>有效样本数（仅平均耗时槽位使用）</summary>
            public long Count;
        }

        /// <summary>由 sum/count 计算平均值（毫秒，四舍五入）</summary>
        private static int ToAvg(long sum, long count)
        {
            if (count <= 0)
            {
                return 0;
            }
            return (int)Math.Round((double)sum / count, MidpointRounding.AwayFromZero);
        }

        private static DbParameter[] BuildTodayParameters(DateTime today)
        {
            return new[]
            {
                DbParameterExtension.CreateDbParameter("@StartTime", today),
                DbParameterExtension.CreateDbParameter("@EndTime", today.AddDays(1))
            };
        }

        private static bool IsFresh(CountSlot slot, DateTime now, bool checkDate)
        {
            lock (slot)
            {
                if (!slot.HasValue || now >= slot.ExpireAt)
                {
                    return false;
                }
                return !checkDate || slot.Key == now.Date;
            }
        }

        private static bool HasValue(CountSlot slot)
        {
            lock (slot)
            {
                return slot.HasValue;
            }
        }

        private static bool HasValueOf(CountSlot slot, DateTime key)
        {
            lock (slot)
            {
                return slot.HasValue && slot.Key == key;
            }
        }

        /// <summary>
        /// 单飞刷新：同一槽位同时只有一个查询在跑，其余调用直接复用该任务。
        /// </summary>
        private Task<int> RefreshAsync(CountSlot slot, DateTime key, TimeSpan ttl, string sql, DbParameter[] parameters)
        {
            lock (slot)
            {
                if (slot.Inflight != null && !slot.Inflight.IsCompleted)
                {
                    return slot.Inflight;
                }
                slot.Inflight = LoadAsync(slot, key, ttl, sql, parameters);
                return slot.Inflight;
            }
        }

        private async Task<int> LoadAsync(CountSlot slot, DateTime key, TimeSpan ttl, string sql, DbParameter[] parameters)
        {
            try
            {
                object obj = await this.BaseRepository().FindObject(sql, parameters).ConfigureAwait(false);
                int value = (obj == null || obj == DBNull.Value) ? 0 : Convert.ToInt32(obj);

                lock (slot)
                {
                    slot.Value = value;
                    slot.Key = key;
                    slot.HasValue = true;
                    slot.ExpireAt = DateTime.Now.Add(ttl);
                }
                return value;
            }
            catch (Exception ex)
            {
                LogHelper.Error("读取搜索统计失败：" + sql, ex);
                lock (slot)
                {
                    // 退避：短时间内不再重复查询；已无值时降级为 0，保证前台可渲染
                    slot.ExpireAt = DateTime.Now.Add(FailureBackoff);
                    if (!slot.HasValue)
                    {
                        slot.Value = 0;
                        slot.Key = key;
                        slot.HasValue = true;
                    }
                }
                return slot.Value;
            }
        }

        /// <summary>
        /// 平均搜索耗时刷新（单飞）：一次性把 sum / count 载入内存，之后靠增量累加维护。
        /// </summary>
        private Task<int> RefreshAvgCostAsync(CountSlot slot, DateTime key, TimeSpan ttl)
        {
            lock (slot)
            {
                if (slot.Inflight != null && !slot.Inflight.IsCompleted)
                {
                    return slot.Inflight;
                }
                slot.Inflight = LoadAvgCostAsync(slot, key, ttl);
                return slot.Inflight;
            }
        }

        private async Task<int> LoadAvgCostAsync(CountSlot slot, DateTime key, TimeSpan ttl)
        {
            try
            {
                object sumObj = await this.BaseRepository().FindObject(SqlAvgSum, null).ConfigureAwait(false);
                object countObj = await this.BaseRepository().FindObject(SqlAvgCount, null).ConfigureAwait(false);

                long sum = (sumObj == null || sumObj == DBNull.Value) ? 0 : Convert.ToInt64(sumObj);
                long count = (countObj == null || countObj == DBNull.Value) ? 0 : Convert.ToInt64(countObj);
                int value = ToAvg(sum, count);

                lock (slot)
                {
                    slot.Sum = sum;
                    slot.Count = count;
                    slot.Value = value;
                    slot.Key = key;
                    slot.HasValue = true;
                    slot.ExpireAt = DateTime.Now.Add(ttl);
                }
                return value;
            }
            catch (Exception ex)
            {
                LogHelper.Error("读取平均搜索耗时失败", ex);
                lock (slot)
                {
                    slot.ExpireAt = DateTime.Now.Add(FailureBackoff);
                    if (!slot.HasValue)
                    {
                        slot.Value = 0;
                        slot.Key = key;
                        slot.HasValue = true;
                    }
                }
                return slot.Value;
            }
        }

        private static PanSearchStatsSnapshot BuildSnapshot(DateTime today)
        {
            int todayCount;
            int totalCount;
            lock (TodaySlot)
            {
                todayCount = TodaySlot.HasValue && TodaySlot.Key == today ? TodaySlot.Value : 0;
            }
            lock (TotalSlot)
            {
                totalCount = TotalSlot.HasValue ? TotalSlot.Value : 0;
            }
            // 并发写入时两条计数可能短暂不一致，取大值兜底，避免"总搜索量 < 今日搜索"
            if (totalCount < todayCount)
            {
                totalCount = todayCount;
            }
            int avgCostMs;
            lock (AvgCostSlot)
            {
                avgCostMs = AvgCostSlot.HasValue ? AvgCostSlot.Value : 0;
            }
            return new PanSearchStatsSnapshot(today, todayCount, totalCount, avgCostMs, DateTime.Now);
        }

        #endregion
    }
}
