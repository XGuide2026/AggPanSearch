using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 02:05
    /// 描 述：业务类
    /// </summary>
    public class PanSourceCategoryBLL
    {
        private PanSourceCategoryService panSourceCategoryService = new PanSourceCategoryService();

        #region 获取数据
        public async Task<TData<List<PanSourceCategoryEntity>>> GetList(PanSourceCategoryListParam param)
        {
            TData<List<PanSourceCategoryEntity>> obj = new TData<List<PanSourceCategoryEntity>>();
            obj.Data = await panSourceCategoryService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanSourceCategoryEntity>>> GetPageList(PanSourceCategoryListParam param, Pagination pagination)
        {
            TData<List<PanSourceCategoryEntity>> obj = new TData<List<PanSourceCategoryEntity>>();
            obj.Data = await panSourceCategoryService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanSourceCategoryEntity>> GetEntity(long id)
        {
            TData<PanSourceCategoryEntity> obj = new TData<PanSourceCategoryEntity>();
            obj.Data = await panSourceCategoryService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }
        #endregion

        #region 提交数据
        public async Task<TData<string>> SaveForm(PanSourceCategoryEntity entity)
        {
            TData<string> obj = new TData<string>();
            await panSourceCategoryService.SaveForm(entity);
            obj.Data = entity.Id.ParseToString();
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            await panSourceCategoryService.DeleteForm(ids);
            obj.Tag = 1;
            return obj;
        }
        #endregion

        #region 私有方法
        #endregion
    }
}
