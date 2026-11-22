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
    /// 日 期：2026-11-03 03:01
    /// 描 述：服务类
    /// </summary>
    public class PanConfService :  RepositoryFactory
    {
        #region 获取数据
        public async Task<List<PanConfEntity>> GetList(PanConfListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.ToList();
        }

        public async Task<List<PanConfEntity>> GetPageList(PanConfListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list= await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanConfEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanConfEntity>(id);
        }

        /// <summary>
        /// 获取全部配置分类（ConfType 去重后的集合）
        /// </summary>
        public async Task<List<int?>> GetConfTypeList()
        {
            var list = await this.BaseRepository().FindList<PanConfEntity>(t => t.ConfType != null);
            return list.Select(t => t.ConfType).Distinct().OrderBy(t => t).ToList();
        }
        #endregion

        #region 提交数据
        public async Task SaveForm(PanConfEntity entity)
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

        public async Task DeleteForm(string ids)
        {
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            await this.BaseRepository().Delete<PanConfEntity>(idArr);
        }

        /// <summary>
        /// 批量保存配置项（按分类或全量，逐个更新 ConfValue/ConfSpec/ConfStatus）
        /// </summary>
        public async Task SaveBatch(List<PanConfEntity> entities)
        {
            if (entities == null || entities.Count == 0)
            {
                return;
            }
            foreach (var item in entities)
            {
                if (item == null || item.Id.IsNullOrZero())
                {
                    continue;
                }
                var exist = await this.BaseRepository().FindEntity<PanConfEntity>(item.Id.Value);
                if (exist == null)
                {
                    continue;
                }
                // 只更新允许用户修改的字段，避免覆盖系统字段
                exist.ConfValue = item.ConfValue;
                if (item.ConfSpec.HasValue)
                {
                    exist.ConfSpec = item.ConfSpec;
                }
                if (item.ConfStatus.HasValue)
                {
                    exist.ConfStatus = item.ConfStatus;
                }
                await this.BaseRepository().Update(exist);
            }
        }
        #endregion

        #region 私有方法
        private Expression<Func<PanConfEntity, bool>> ListFilter(PanConfListParam param)
        {
            var expression = LinqExtensions.True<PanConfEntity>();
            if (param != null)
            {
                if (param.ConfType.HasValue)
                {
                    expression = expression.And(t => t.ConfType == param.ConfType.Value);
                }
                if (!string.IsNullOrEmpty(param.ConfKey))
                {
                    expression = expression.And(t => t.ConfKey.Contains(param.ConfKey));
                }
                if (!string.IsNullOrEmpty(param.ConfTitle))
                {
                    expression = expression.And(t => t.ConfTitle.Contains(param.ConfTitle));
                }
            }
            return expression;
        }
        #endregion
    }
}