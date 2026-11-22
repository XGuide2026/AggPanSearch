using System;
using System.Linq;
using System.Text;
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
    /// 日 期：2026-11-04 02:05
    /// 描 述：服务类
    /// </summary>
    public class PanSourceCategoryService :  RepositoryFactory
    {
        #region 获取数据
        public async Task<List<PanSourceCategoryEntity>> GetList(PanSourceCategoryListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.ToList();
        }

        public async Task<List<PanSourceCategoryEntity>> GetPageList(PanSourceCategoryListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list= await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanSourceCategoryEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanSourceCategoryEntity>(id);
        }
        #endregion

        #region 提交数据
        public async Task SaveForm(PanSourceCategoryEntity entity)
        {
            if (entity.Id.IsNullOrZero())
            {
                await entity.Create();
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
            await this.BaseRepository().Delete<PanSourceCategoryEntity>(idArr);
        }
        #endregion

        #region 私有方法
        private Expression<Func<PanSourceCategoryEntity, bool>> ListFilter(PanSourceCategoryListParam param)
        {
            var expression = LinqExtensions.True<PanSourceCategoryEntity>();
            if (param != null)
            {
                if (!string.IsNullOrEmpty(param.StartTime.ParseToString()))
                {
                    expression = expression.And(t => t.BaseCreateTime >= param.StartTime);
                }
                if (!string.IsNullOrEmpty(param.EndTime.ParseToString()))
                {
                    param.EndTime = param.EndTime.Value.Date.Add(new TimeSpan(23, 59, 59));
                    expression = expression.And(t => t.BaseCreateTime <= param.EndTime);
                }
            }
            return expression;
        }
        #endregion
    }
}
