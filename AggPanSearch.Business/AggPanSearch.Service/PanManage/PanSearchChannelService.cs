using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Data.Repository;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Service.PanManage
{
    /// <summary>
    /// 描 述：TG频道服务类（pan_search_channel）
    /// </summary>
    public class PanSearchChannelService : RepositoryFactory
    {
        #region 获取数据
        public async Task<List<PanSearchChannelEntity>> GetList(PanSearchChannelListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.OrderBy(t => t.Sort ?? 0).ThenBy(t => t.Id).ToList();
        }

        public async Task<List<PanSearchChannelEntity>> GetPageList(PanSearchChannelListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanSearchChannelEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanSearchChannelEntity>(id);
        }

        /// <summary>
        /// 读取参与前台搜索的频道名（Status=1），按 Sort 升序；空列表表示未配置。
        /// </summary>
        public async Task<List<string>> GetEnabledNames()
        {
            var expression = LinqExtensions.True<PanSearchChannelEntity>();
            expression = expression.And(t => t.Status == 1);
            var list = await this.BaseRepository().FindList(expression);
            return list.OrderBy(t => t.Sort ?? 0)
                       .ThenBy(t => t.Id)
                       .Select(t => NormalizeName(t.Name))
                       .Where(t => t.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        /// <summary>
        /// 频道名是否已存在（新增/编辑时校验，忽略大小写由数据库唯一索引保证）
        /// </summary>
        public bool ExistName(PanSearchChannelEntity entity)
        {
            string name = NormalizeName(entity?.Name);
            if (name.Length == 0)
            {
                return false;
            }
            var expression = LinqExtensions.True<PanSearchChannelEntity>();
            if (entity.Id.IsNullOrZero())
            {
                expression = expression.And(t => t.Name == name);
            }
            else
            {
                expression = expression.And(t => t.Name == name && t.Id != entity.Id);
            }
            return this.BaseRepository().IQueryable(expression).Count() > 0;
        }
        #endregion

        #region 提交数据
        public async Task SaveForm(PanSearchChannelEntity entity)
        {
            entity.Name = NormalizeName(entity.Name);
            if (entity.Id.IsNullOrZero())
            {
                // BaseModifyEntity 的 Create() 只写 BaseCreateTime，不会回填 BaseModifyTime，
                // 不补 Modify() 的话「更新时间」会取到数据库默认值 1970-01-01
                await entity.Create();
                await entity.Modify();
                await this.BaseRepository().Insert(entity);
            }
            else
            {
                await entity.Modify();
                await this.BaseRepository().Update(entity);
            }
        }

        public async Task DeleteForm(string ids)
        {
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            await this.BaseRepository().Delete<PanSearchChannelEntity>(idArr);
        }

        /// <summary>
        /// 重置系统默认频道：先清空表内全部数据，再重新写入系统默认频道（Status=1 全部启用）。
        /// 返回写入条数；系统默认清单为空时不做任何改动并返回 0。
        /// </summary>
        public async Task<int> ResetDefaults(IEnumerable<string> names)
        {
            List<string> channelNames = (names ?? Enumerable.Empty<string>())
                .Select(NormalizeName)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (channelNames.Count == 0)
            {
                // 清单异常为空时直接放弃，避免把用户已维护的数据清空
                return 0;
            }

            List<PanSearchChannelEntity> entities = new List<PanSearchChannelEntity>();
            int sort = 0;
            foreach (string name in channelNames)
            {
                PanSearchChannelEntity entity = new PanSearchChannelEntity
                {
                    Name = name,
                    Category = "默认",
                    Sort = ++sort,
                    Status = 1,
                    IsSys = 1,
                    Weight = 0,
                    Remark = "PanSearch 内置默认频道"
                };
                // 同 SaveForm：Create() 不回填 BaseModifyTime，需补 Modify()，否则更新时间是 1970-01-01
                await entity.Create();
                await entity.Modify();
                entities.Add(entity);
            }

            // 删除与重新写入放在同一事务里，避免中途失败导致表被清空
            var db = await this.BaseRepository().BeginTrans();
            try
            {
                await db.Delete<PanSearchChannelEntity>();
                await db.Insert(entities);
                await db.CommitTrans();
            }
            catch
            {
                await db.RollbackTrans();
                throw;
            }
            return entities.Count;
        }

        /// <summary>
        /// 保存后台探测结果：可访问时刷新 LastOkTime，抓到网盘资源时间时刷新 LastUpdate。
        /// ProbeStatus 记录本次结果（1可访问 0不可访问），供「检测资源」筛选使用。
        /// 不改 BaseModifyTime，避免自动检测把列表的「更新时间」刷成当前时间。
        /// </summary>
        public async Task SaveProbeResult(long id, bool accessible, DateTime? lastUpdate)
        {
            PanSearchChannelEntity entity = await GetEntity(id);
            if (entity == null)
            {
                return;
            }
            entity.ProbeStatus = accessible ? 1 : 0;
            if (accessible)
            {
                entity.LastOkTime = DateTime.Now;
            }
            if (lastUpdate.HasValue)
            {
                entity.LastUpdate = lastUpdate;
            }
            await this.BaseRepository().Update(entity);
        }
        #endregion

        #region 私有方法
        /// <summary>
        /// 规范化频道名：去掉完整链接前缀、@ 符号、首尾空白与末尾斜杠
        /// </summary>
        public static string NormalizeName(string value)
        {
            string name = (value ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return string.Empty;
            }
            name = name.Replace("https://t.me/s/", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("http://t.me/s/", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("https://t.me/", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("http://t.me/", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("t.me/s/", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("t.me/", "", StringComparison.OrdinalIgnoreCase)
                       .TrimStart('@')
                       .Trim();
            return name.TrimEnd('/');
        }

        private Expression<Func<PanSearchChannelEntity, bool>> ListFilter(PanSearchChannelListParam param)
        {
            var expression = LinqExtensions.True<PanSearchChannelEntity>();
            if (param != null)
            {
                if (!string.IsNullOrWhiteSpace(param.name))
                {
                    string name = param.name.Trim();
                    expression = expression.And(t => t.Name.Contains(name));
                }
                if (!string.IsNullOrWhiteSpace(param.category))
                {
                    string category = param.category.Trim();
                    expression = expression.And(t => t.Category == category);
                }
                if (param.status != null)
                {
                    int status = param.status.Value;
                    expression = expression.And(t => t.Status == status);
                }
                expression = ApplyProbeFilter(expression, param.probe);
            }
            return expression;
        }

        /// <summary>
        /// 「检测资源」筛选（硬编码可选值）：
        /// today=今日有更新 none=无网盘资源 inaccessible=不可访问
        /// week=最近1周 month=最近1个月 halfyear=最近6个月 year=最近一年
        /// </summary>
        private static Expression<Func<PanSearchChannelEntity, bool>> ApplyProbeFilter(
            Expression<Func<PanSearchChannelEntity, bool>> expression, string probe)
        {
            if (string.IsNullOrWhiteSpace(probe))
            {
                return expression;
            }
            DateTime now = DateTime.Now;
            switch (probe.Trim().ToLowerInvariant())
            {
                case "today":
                    {
                        DateTime today = DateTime.Today;
                        return expression.And(t => t.LastUpdate != null && t.LastUpdate.Value >= today);
                    }
                case "week":
                    {
                        DateTime from = now.AddDays(-7);
                        return expression.And(t => t.LastUpdate != null && t.LastUpdate.Value >= from);
                    }
                case "month":
                    {
                        DateTime from = now.AddMonths(-1);
                        return expression.And(t => t.LastUpdate != null && t.LastUpdate.Value >= from);
                    }
                case "halfyear":
                    {
                        DateTime from = now.AddMonths(-6);
                        return expression.And(t => t.LastUpdate != null && t.LastUpdate.Value >= from);
                    }
                case "year":
                    {
                        DateTime from = now.AddYears(-1);
                        return expression.And(t => t.LastUpdate != null && t.LastUpdate.Value >= from);
                    }
                case "none":
                    // 无网盘资源：没有资源时间，且不是「不可访问」（未检测过的也算在内，SQL 中 NULL != 0 不成立，需显式判断）
                    return expression.And(t => t.LastUpdate == null && (t.ProbeStatus == null || t.ProbeStatus != 0));
                case "inaccessible":
                    return expression.And(t => t.ProbeStatus == 0);
                default:
                    return expression;
            }
        }
        #endregion
    }
}