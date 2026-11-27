using System;
using System.Linq;
using System.Text;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Data;
using AggPanSearch.Data.Repository;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Service.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 03:11
    /// 描 述：服务类
    /// </summary>
    public class PanSourceService :  RepositoryFactory
    {
        #region 获取数据
        public async Task<List<PanSourceEntity>> GetList(PanSourceListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.ToList();
        }

        public async Task<List<PanSourceEntity>> GetPageList(PanSourceListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list= await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        /// <summary>
        /// 前台资源分类页分页列表：过滤软删除与后台禁用记录，按 Id 倒序
        /// categoryId 大于 0 时只取该分类下的资源
        /// </summary>
        public async Task<List<PanSourceEntity>> GetCategoryList(Pagination pagination, long categoryId = 0)
        {
            var expression = LinqExtensions.True<PanSourceEntity>();
            // 排除软删除(IsDelete=1)与后台禁用(Status=0)的记录，null 视为正常
            expression = expression.And(t => t.IsDelete == null || t.IsDelete != 1);
            expression = expression.And(t => t.Status == null || t.Status != 0);
            if (categoryId > 0)
            {
                // pan_source.SourceCategoryId 是 int 列，而 pan_source_category.Id 是 bigint（后台新增为雪花 ID）。
                // 超出 int 范围的分类 ID 不可能有归属资源，直接返回空集合，避免强转溢出。
                if (categoryId > int.MaxValue)
                {
                    return new List<PanSourceEntity>();
                }
                int sourceCategoryId = (int)categoryId;
                expression = expression.And(t => t.SourceCategoryId == sourceCategoryId);
            }
            pagination.Sort = "Id";
            pagination.SortType = " desc ";
            var list = await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanSourceEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanSourceEntity>(id);
        }

        /// <summary>
        /// 按 Id 批量查询资源（自动截图等批量任务用），分批 IN 查询避免逐条查库；软删除记录不参与。
        /// </summary>
        public async Task<List<PanSourceEntity>> GetListByIds(List<long> ids)
        {
            List<PanSourceEntity> result = new List<PanSourceEntity>();
            if (ids == null || ids.Count == 0)
            {
                return result;
            }

            const int batchSize = 200;
            for (int i = 0; i < ids.Count; i += batchSize)
            {
                List<long> batch = ids.Skip(i).Take(batchSize).ToList();
                var expression = LinqExtensions.True<PanSourceEntity>();
                expression = expression.And(t => batch.Contains(t.Id.Value));
                expression = expression.And(t => t.IsDelete == null || t.IsDelete != 1);
                List<PanSourceEntity> list = (await this.BaseRepository().FindList(expression)).ToList();
                result.AddRange(list);
            }
            return result;
        }

        /// <summary>
        /// 按分类统计资源数量（pan_source.SourceCategoryId 分组）：
        /// 键为分类ID（int），值为排除软删除/禁用后的资源条数，供前台分类导航显示数量标签。
        /// </summary>
        public async Task<Dictionary<int, int>> GetCategoryCounts()
        {
            var map = new Dictionary<int, int>();
            try
            {
                string sql = "SELECT SourceCategoryId AS category_id, COUNT(*) AS cnt FROM pan_source "
                           + "WHERE SourceCategoryId IS NOT NULL "
                           + "AND (IsDelete IS NULL OR IsDelete <> 1) "
                           + "AND (Status IS NULL OR Status <> 0) "
                           + "GROUP BY SourceCategoryId";
                var table = await this.BaseRepository().FindTable(sql);
                if (table != null)
                {
                    foreach (DataRow row in table.Rows)
                    {
                        if (row["category_id"] == DBNull.Value)
                        {
                            continue;
                        }
                        int categoryId = Convert.ToInt32(row["category_id"]);
                        if (categoryId > 0)
                        {
                            map[categoryId] = Convert.ToInt32(row["cnt"]);
                        }
                    }
                }
            }
            catch
            {
                // pan_source 表不存在或数据库不可用时返回空字典，前台不显示数量
            }
            return map;
        }

        /// <summary>
        /// 统计「未分类」资源数量（SourceCategoryId 为空或 0），排除软删除与禁用资源，
        /// 与后台 ListFilter 中 source_category_id == 0 的筛选判定保持一致。
        /// </summary>
        public async Task<int> GetUncategorizedCount()
        {
            try
            {
                string sql = "SELECT COUNT(*) AS cnt FROM pan_source "
                           + "WHERE (SourceCategoryId IS NULL OR SourceCategoryId = 0) "
                           + "AND (IsDelete IS NULL OR IsDelete <> 1) "
                           + "AND (Status IS NULL OR Status <> 0)";
                var table = await this.BaseRepository().FindTable(sql);
                if (table != null && table.Rows.Count > 0)
                {
                    return Convert.ToInt32(table.Rows[0]["cnt"]);
                }
            }
            catch
            {
                // pan_source 表不存在或数据库不可用时返回 0
            }
            return 0;
        }

        /// <summary>
        /// 按原始分享链接（存于 OriginalUrl 字段）判断本地资源是否已转存过，已软删除的记录不参与判重。
        /// </summary>
        public async Task<bool> ExistsByOriginalUrl(string originalUrl)
        {
            if (string.IsNullOrWhiteSpace(originalUrl))
            {
                return false;
            }
            string url = originalUrl.Trim();
            var list = await this.BaseRepository().FindList<PanSourceEntity>(
                t => t.OriginalUrl == url && (t.IsDelete == null || t.IsDelete != 1));
            return list.Any();
        }

        /// <summary>
        /// 按原始分享链接（存于 OriginalUrl 字段）取本地资源记录，已软删除的记录不参与。
        /// </summary>
        public async Task<PanSourceEntity> GetByOriginalUrl(string originalUrl)
        {
            if (string.IsNullOrWhiteSpace(originalUrl))
            {
                return null;
            }
            string url = originalUrl.Trim();
            var list = await this.BaseRepository().FindList<PanSourceEntity>(
                t => t.OriginalUrl == url && (t.IsDelete == null || t.IsDelete != 1));
            return list.FirstOrDefault();
        }

        /// <summary>更新本地资源的分享地址（只更新 Url 字段）。</summary>
        public async Task UpdateUrl(long id, string url)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            PanSourceEntity entity = new PanSourceEntity
            {
                Id = id,
                Url = url,
            };
            await this.BaseRepository().Update(entity);
        }
        #endregion

        #region 提交数据
        public async Task SaveForm(PanSourceEntity entity)
        {
            if (entity.Id.IsNullOrZero())
            {
                entity.Create();
                await this.BaseRepository().Insert(entity);
            }
            else
            {
                
                await this.BaseRepository().Update(entity);
            }
        }

        /// <summary>
        /// 新增一条由「转存并分享」生成的本地资源（只做插入，不做查重，判重由调用方负责）。
        /// </summary>
        public async Task SaveTransfer(PanSourceEntity entity)
        {
            if (entity == null)
            {
                return;
            }
            await entity.Create();
            await this.BaseRepository().Insert(entity);
        }

        public async Task DeleteForm(string ids)
        {
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            await this.BaseRepository().Delete<PanSourceEntity>(idArr);
        }

        /// <summary>
        /// 只更新资源图片一列（自动截图回写用）：构造只带 Id 与 VodPic 的实体做局部更新，
        /// 避免整行覆盖用户同时编辑的其他字段。
        /// </summary>
        public async Task UpdateVodPic(long id, string vodPic)
        {
            if (id <= 0)
            {
                return;
            }
            PanSourceEntity entity = new PanSourceEntity
            {
                Id = id,
                VodPic = vodPic ?? string.Empty,
                BaseModifyTime = DateTime.Now,
            };
            // 不走 entity.Modify()：截图任务跑在后台线程，拿不到当前登录用户（与 UpdateUrl 一致）
            await this.BaseRepository().Update(entity);
        }
        #endregion

        #region 私有方法
        private Expression<Func<PanSourceEntity, bool>> ListFilter(PanSourceListParam param)
        {
            var expression = LinqExtensions.True<PanSourceEntity>();
            if (param != null)
            {
                // 资源分类筛选（查询下拉"全部"时传 -1，不参与过滤）
                if (param.source_category_id != null && param.source_category_id > 0)
                {
                    expression = expression.And(t => t.SourceCategoryId == param.source_category_id);
                }
                // 传 0 表示筛选「未分类」：分类为空或 0 视为未分类，与前台卡片标签的判定保持一致
                else if (param.source_category_id != null && param.source_category_id == 0)
                {
                    expression = expression.And(t => t.SourceCategoryId == null || t.SourceCategoryId == 0);
                }
                // 关键词模糊搜索（资源名称 / 资源地址 / 资源介绍）
                if (!string.IsNullOrEmpty(param.keyword))
                {
                    expression = expression.And(t => t.Title.Contains(param.keyword)
                        || t.Url.Contains(param.keyword)
                        || t.VodContent.Contains(param.keyword));
                }
            }
            return expression;
        }
        #endregion
    }
}