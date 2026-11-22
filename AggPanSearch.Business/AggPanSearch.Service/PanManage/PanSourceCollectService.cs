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
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.OrderByDescending(t => t.BaseCreateTime).ThenByDescending(t => t.Id).ToList();
        }

        public async Task<List<PanSourceCollectEntity>> GetPageList(PanSourceCollectListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
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

        /// <summary>
        /// 查询缺少本地图片的采集记录（ImageUrl 为空且 CollectImageUrl 非空），
        /// 按采集时间倒序取前 limit 条，供后台补图任务使用。
        /// </summary>
        public async Task<List<PanSourceCollectEntity>> GetImagePendingList(int limit)
        {
            var expression = LinqExtensions.True<PanSourceCollectEntity>();
            expression = expression.And(t => t.ImageUrl == null || t.ImageUrl == "");
            expression = expression.And(t => t.CollectImageUrl != null && t.CollectImageUrl != "");
            List<PanSourceCollectEntity> list = (await this.BaseRepository().FindList(expression)).ToList();
            return list
                .OrderByDescending(t => t.BaseCreateTime)
                .ThenByDescending(t => t.Id)
                .Take(limit > 0 ? limit : 50)
                .ToList();
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
                if (param.is_link_valid != null && param.is_link_valid.Value >= 0)
                {
                    int valid = param.is_link_valid.Value;
                    expression = expression.And(t => t.IsLinkValid == valid);
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

        #endregion
    }
}