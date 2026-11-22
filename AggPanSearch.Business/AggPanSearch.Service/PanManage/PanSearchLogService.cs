using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Data;
using AggPanSearch.Data.Repository;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Service.PanManage
{
    /// <summary>
    /// 描 述：搜索日志数据服务（pan_search_log）
    /// </summary>
    public class PanSearchLogService : RepositoryFactory
    {
        // 统计走带缓存的服务，避免前台每次刷新首页都 COUNT 全表
        private static readonly PanSearchStatsService StatsService = new PanSearchStatsService();

        #region 获取数据

        public async Task<List<PanSearchLogEntity>> GetList(PanSearchLogListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.ToList();
        }

        public async Task<List<PanSearchLogEntity>> GetPageList(PanSearchLogListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanSearchLogEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanSearchLogEntity>(id);
        }

        /// <summary>
        /// 今日搜索量（当天 00:00:00 ~ 次日 00:00:00），走内存缓存，见 PanSearchStatsService
        /// </summary>
        public async Task<int> GetTodayCount()
        {
            return await StatsService.GetTodayCountAsync();
        }

        /// <summary>
        /// 总搜索量，走内存缓存，见 PanSearchStatsService
        /// </summary>
        public async Task<int> GetTotalCount()
        {
            return await StatsService.GetTotalCountAsync();
        }

        /// <summary>
        /// 平均搜索耗时（毫秒）：pan_search_log.cost_ms 的平均值（含缓存命中），走内存缓存
        /// </summary>
        public async Task<int> GetAvgCost()
        {
            return await StatsService.GetAvgCostAsync();
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 写入一条搜索日志（供 /api/other/web_search 调用）
        /// </summary>
        /// <param name="keyword">搜索关键词</param>
        /// <param name="ip">搜索来源IP</param>
        /// <param name="isCache">是否命中缓存</param>
        /// <param name="resultTotal">搜索结果总数</param>
        /// <param name="panTypeResults">分类网盘搜索结果，形如 夸克网盘:12;百度网盘:3</param>
        /// <param name="costMs">搜索耗时（毫秒）</param>
        public async Task AddLog(string keyword, string ip, bool isCache = false, int resultTotal = 0,
            string panTypeResults = null, int costMs = 0)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return;
            }
            PanSearchLogEntity entity = new PanSearchLogEntity();
            entity.Keyword = keyword.Length > 255 ? keyword.Substring(0, 255) : keyword;
            entity.Ip = string.IsNullOrWhiteSpace(ip) ? null : (ip.Length > 64 ? ip.Substring(0, 64) : ip);
            entity.IsCache = isCache ? 1 : 0;
            entity.ResultTotal = resultTotal;
            entity.PanTypeResults = string.IsNullOrWhiteSpace(panTypeResults)
                ? null
                : (panTypeResults.Length > 1000 ? panTypeResults.Substring(0, 1000) : panTypeResults);
            entity.CostMs = costMs;
            entity.CreateTime = DateTime.Now;
            entity.Create();
            await this.BaseRepository().Insert(entity);
            // 写入成功后本地累加，前台"今日搜索/总搜索量/平均耗时"立即体现，且不产生额外查询
            StatsService.OnSearchRecorded(costMs);
        }

        public async Task SaveForm(PanSearchLogEntity entity)
        {
            if (entity == null)
            {
                return;
            }
            if (entity.Id.IsNullOrZero())
            {
                entity.Create();
                if (entity.CreateTime == null)
                {
                    entity.CreateTime = DateTime.Now;
                }
                await this.BaseRepository().Insert(entity);
            }
            else
            {
                await this.BaseRepository().Update(entity);
            }
        }

        public async Task DeleteForm(long[] ids)
        {
            await this.BaseRepository().Delete<PanSearchLogEntity>(ids);
            StatsService.Invalidate();
        }

        /// <summary>
        /// 清空日志（truncate 后雪花ID不回退，符合本项目主键策略）
        /// </summary>
        public async Task ClearForm()
        {
            await this.BaseRepository().ExecuteBySql("truncate table pan_search_log");
            StatsService.Invalidate();
        }

        #endregion

        #region 私有方法

        private Expression<Func<PanSearchLogEntity, bool>> ListFilter(PanSearchLogListParam param)
        {
            var expression = LinqExtensions.True<PanSearchLogEntity>();
            if (param != null)
            {
                if (!string.IsNullOrWhiteSpace(param.keyword))
                {
                    expression = expression.And(t => t.Keyword.Contains(param.keyword));
                }
                if (!string.IsNullOrWhiteSpace(param.ip))
                {
                    expression = expression.And(t => t.Ip.Contains(param.ip));
                }
                // 是否命中缓存：-1（全部）或空 不参与过滤
                if (param.is_cache != null && param.is_cache.Value >= 0)
                {
                    int isCache = param.is_cache.Value == 1 ? 1 : 0;
                    expression = expression.And(t => t.IsCache == isCache);
                }
                if (param.startTime != null)
                {
                    DateTime start = param.startTime.Value.Date;
                    expression = expression.And(t => t.CreateTime >= start);
                }
                if (param.endTime != null)
                {
                    DateTime end = param.endTime.Value.Date.AddDays(1);
                    expression = expression.And(t => t.CreateTime < end);
                }
            }
            return expression;
        }

        #endregion
    }
}
