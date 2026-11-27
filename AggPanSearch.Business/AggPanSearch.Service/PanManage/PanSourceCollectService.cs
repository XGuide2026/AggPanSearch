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
    /// 描 述：采集资源数据服务（pan_source_collect）
    /// </summary>
    public class PanSourceCollectService : RepositoryFactory
    {
        #region 获取数据

        public async Task<List<PanSourceCollectEntity>> GetList(PanSourceCollectListParam param)
        {
            // 子查询与主查询必须基于同一个 dbContext 实例翻译，故此处复用同一个 repository
            var repository = this.BaseRepository();
            var expression = ListFilter(param);
            expression = AppendSaveFilter(expression, param, repository);
            var list = await repository.FindList(expression);

            List<PanSourceCollectEntity> result;
            // 按文件大小排序（单位 M）：NULL（未分析）始终排最后；未指定时保持按采集时间倒序
            if (param != null && param.file_size_sort == "asc")
            {
                result = list.OrderBy(t => t.FileSize == null).ThenBy(t => t.FileSize).ThenByDescending(t => t.BaseCreateTime).ToList();
            }
            else if (param != null && param.file_size_sort == "desc")
            {
                result = list.OrderBy(t => t.FileSize == null).ThenByDescending(t => t.FileSize).ThenByDescending(t => t.BaseCreateTime).ToList();
            }
            else
            {
                result = list.OrderByDescending(t => t.BaseCreateTime).ThenByDescending(t => t.Id).ToList();
            }

            await MarkSaveFlags(result);
            return result;
        }

        public async Task<List<PanSourceCollectEntity>> GetPageList(PanSourceCollectListParam param, Pagination pagination)
        {
            // 子查询与主查询必须基于同一个 dbContext 实例翻译，故此处复用同一个 repository
            var repository = this.BaseRepository();
            var expression = ListFilter(param);
            expression = AppendSaveFilter(expression, param, repository);

            // 文件大小排序需覆盖默认的采集时间倒序，否则 SQL 层分页排序不生效
            if (param != null && param.file_size_sort == "asc")
            {
                pagination.Sort = "FileSize";
                pagination.SortType = "asc";
            }
            else if (param != null && param.file_size_sort == "desc")
            {
                pagination.Sort = "FileSize";
                pagination.SortType = "desc";
            }

            var list = await repository.FindList(expression, pagination);
            List<PanSourceCollectEntity> result = list.ToList();
            await MarkSaveFlags(result);
            return result;
        }

        public async Task<PanSourceCollectEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanSourceCollectEntity>(id);
        }

        /// <summary>
        /// 按 Id 批量查询采集记录（分批 IN 查询，避免逐条查库）。
        /// </summary>
        public async Task<List<PanSourceCollectEntity>> GetListByIds(List<long> ids)
        {
            List<PanSourceCollectEntity> result = new List<PanSourceCollectEntity>();
            if (ids == null || ids.Count == 0)
            {
                return result;
            }

            const int batchSize = 200;
            for (int i = 0; i < ids.Count; i += batchSize)
            {
                List<long> batch = ids.Skip(i).Take(batchSize).ToList();
                var expression = LinqExtensions.True<PanSourceCollectEntity>();
                expression = expression.And(t => batch.Contains(t.Id.Value));
                List<PanSourceCollectEntity> list = (await this.BaseRepository().FindList(expression)).ToList();
                result.AddRange(list);
            }
            return result;
        }

        #endregion

        #region 提交数据

        public async Task SaveForm(PanSourceCollectEntity entity)
        {
            if (entity.Id.IsNullOrZero())
            {
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

        /// <summary>
        /// 查询已存在的资源链接（按 ResourceUrl 分批 IN 查询），用于采集入库前判重。
        /// </summary>
        public async Task<HashSet<string>> GetExistingUrls(List<string> urls)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (urls == null || urls.Count == 0)
            {
                return result;
            }

            const int batchSize = 200;
            for (int i = 0; i < urls.Count; i += batchSize)
            {
                List<string> batch = urls.Skip(i).Take(batchSize).ToList();
                var expression = LinqExtensions.True<PanSourceCollectEntity>();
                expression = expression.And(t => batch.Contains(t.ResourceUrl));
                List<PanSourceCollectEntity> list = (await this.BaseRepository().FindList(expression)).ToList();
                foreach (PanSourceCollectEntity entity in list)
                {
                    if (!string.IsNullOrEmpty(entity.ResourceUrl))
                    {
                        result.Add(entity.ResourceUrl);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 批量插入采集结果（自动补齐雪花 Id 与基类字段，按 200 条分批提交）。
        /// </summary>
        public async Task InsertBatch(List<PanSourceCollectEntity> entities)
        {
            if (entities == null || entities.Count == 0)
            {
                return;
            }

            foreach (PanSourceCollectEntity entity in entities)
            {
                await entity.Create();
                await entity.Modify();
            }

            const int batchSize = 200;
            for (int i = 0; i < entities.Count; i += batchSize)
            {
                List<PanSourceCollectEntity> batch = entities.Skip(i).Take(batchSize).ToList();
                await this.BaseRepository().Insert(batch);
            }
        }

        /// <summary>
        /// 只更新原图片与本地图片两列（补图任务回写用）：构造只带 Id 与两个图片字段的实体做局部更新，
        /// 避免整行覆盖用户同时编辑的其他字段。
        /// </summary>
        public async Task UpdateImages(long id, string collectImageUrl, string imageUrl)
        {
            if (id <= 0)
            {
                return;
            }
            PanSourceCollectEntity entity = new PanSourceCollectEntity
            {
                Id = id,
                CollectImageUrl = collectImageUrl ?? string.Empty,
                ImageUrl = imageUrl ?? string.Empty,
            };
            await entity.Modify();
            await this.BaseRepository().Update(entity);
        }

        /// <summary>
        /// 只更新「资源信息分析」三列（文件个数 / 文件大小 / 资源有效期），可选附带链接有效性（IsLinkValid）：
        /// 构造只带 Id 与相关字段的实体做局部更新，避免整行覆盖用户同时编辑的其他字段。
        /// </summary>
        public async Task UpdateAnalyzeInfo(long id, int? fileCount, int? fileSize, string shareValidityPeriod, int? linkValid = null)
        {
            if (id <= 0)
            {
                return;
            }
            PanSourceCollectEntity entity = new PanSourceCollectEntity
            {
                Id = id,
                FileCount = fileCount,
                FileSize = fileSize,
                ShareValidityPeriod = shareValidityPeriod ?? string.Empty,
                // linkValid 为 null 时不更新该列，保持向后兼容
                IsLinkValid = linkValid,
            };
            await entity.Modify();
            await this.BaseRepository().Update(entity);
        }

        /// <summary>
        /// 只更新链接有效性一列（IsLinkValid），供资源分析失败 / 异常分支回写 0，不清空已解析字段。
        /// </summary>
        public async Task UpdateLinkValid(long id, int valid)
        {
            if (id <= 0)
            {
                return;
            }
            PanSourceCollectEntity entity = new PanSourceCollectEntity
            {
                Id = id,
                IsLinkValid = valid,
            };
            await entity.Modify();
            await this.BaseRepository().Update(entity);
        }

        public async Task DeleteForm(string ids)
        {
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                return;
            }
            await this.BaseRepository().Delete<PanSourceCollectEntity>(idArr);
        }

        #endregion

        #region 私有方法

        private Expression<Func<PanSourceCollectEntity, bool>> ListFilter(PanSourceCollectListParam param)
        {
            var expression = LinqExtensions.True<PanSourceCollectEntity>();
            if (param != null)
            {
                if (!string.IsNullOrWhiteSpace(param.collect_source))
                {
                    string collectSource = param.collect_source.Trim();
                    expression = expression.And(t => t.CollectSource.Contains(collectSource));
                }
                if (param.source_category_id != null && param.source_category_id.Value > 0)
                {
                    long categoryId = param.source_category_id.Value;
                    expression = expression.And(t => t.SourceCategoryId == categoryId);
                }
                if (!string.IsNullOrWhiteSpace(param.resource_name))
                {
                    string resourceName = param.resource_name.Trim();
                    expression = expression.And(t => t.ResourceName.Contains(resourceName));
                }
                if (param.is_link_valid != null)
                {
                    int valid = param.is_link_valid.Value;
                    if (valid == -2)
                    {
                        // 前端「未检测」筛选用 -2，映射数据库值 -1（严格匹配，不含 NULL 旧数据）
                        expression = expression.And(t => t.IsLinkValid == -1);
                    }
                    else if (valid >= 0)
                    {
                        expression = expression.And(t => t.IsLinkValid == valid);
                    }
                    // 其余负值（含「全部」-1）不限定链接状态
                }
                if (param.startTime != null)
                {
                    DateTime start = param.startTime.Value.Date;
                    expression = expression.And(t => t.BaseCreateTime >= start);
                }
                if (param.endTime != null)
                {
                    DateTime end = param.endTime.Value.Date.AddDays(1);
                    expression = expression.And(t => t.BaseCreateTime < end);
                }
            }
            return expression;
        }

        /// <summary>
        /// 追加「转存本地」筛选：1 已转存 / 0 未转存。
        /// 已转存判定与 MarkSaveFlags 完全一致：pan_source 中存在 OriginalUrl 相同且未软删除的记录。
        /// 用 EF 相关子查询（EXISTS）在 SQL 层直接过滤，保证分页场景下筛选正确；
        /// ResourceUrl 为空的记录永远不会匹配 Any，因此自然归为「未转存」。
        /// 注意：子查询必须与主查询基于同一个 dbContext 实例翻译，故传入当前正在使用的 repository。
        /// </summary>
        private Expression<Func<PanSourceCollectEntity, bool>> AppendSaveFilter(
            Expression<Func<PanSourceCollectEntity, bool>> expression,
            PanSourceCollectListParam param,
            Repository repository)
        {
            if (param == null || param.is_save == null || (param.is_save.Value != 1 && param.is_save.Value != 0))
            {
                return expression;
            }

            if (param.is_save.Value == 1)
            {
                expression = expression.And(t => repository.db.dbContext.Set<PanSourceEntity>()
                    .Any(p => p.OriginalUrl == t.ResourceUrl && (p.IsDelete == null || p.IsDelete != 1)));
            }
            else
            {
                expression = expression.And(t => !repository.db.dbContext.Set<PanSourceEntity>()
                    .Any(p => p.OriginalUrl == t.ResourceUrl && (p.IsDelete == null || p.IsDelete != 1)));
            }
            return expression;
        }

        /// <summary>
        /// 批量回填 IsSave：收集列表内全部 ResourceUrl（去空、去重），分批 IN 查询本地资源表 pan_source 的
        /// OriginalUrl（转存原链接，排除软删除）命中集合，命中的采集记录标记 IsSave = true。
        /// 转存时 pan_source.OriginalUrl 写入的正是采集资源 ResourceUrl 原文，因此可精确判定「已转存」。
        /// 一次批量查询避免 N+1 逐条 EXISTS。
        /// </summary>
        private async Task MarkSaveFlags(List<PanSourceCollectEntity> list)
        {
            if (list == null || list.Count == 0)
            {
                return;
            }

            List<string> urls = list
                .Where(t => !string.IsNullOrWhiteSpace(t.ResourceUrl))
                .Select(t => t.ResourceUrl.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (urls.Count == 0)
            {
                return;
            }

            HashSet<string> savedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int batchSize = 200;
            for (int i = 0; i < urls.Count; i += batchSize)
            {
                List<string> batch = urls.Skip(i).Take(batchSize).ToList();
                var expression = LinqExtensions.True<PanSourceEntity>();
                expression = expression.And(t => batch.Contains(t.OriginalUrl) && (t.IsDelete == null || t.IsDelete != 1));
                List<PanSourceEntity> existed = (await this.BaseRepository().FindList(expression)).ToList();
                foreach (PanSourceEntity entity in existed)
                {
                    if (!string.IsNullOrWhiteSpace(entity.OriginalUrl))
                    {
                        savedUrls.Add(entity.OriginalUrl.Trim());
                    }
                }
            }
            if (savedUrls.Count == 0)
            {
                return;
            }

            foreach (PanSourceCollectEntity entity in list)
            {
                if (!string.IsNullOrWhiteSpace(entity.ResourceUrl) && savedUrls.Contains(entity.ResourceUrl.Trim()))
                {
                    entity.IsSave = true;
                }
            }
        }

        #endregion
    }
}