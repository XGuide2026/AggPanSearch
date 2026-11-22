using System;
using System.Collections.Generic;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集站点业务类。
    /// 站点信息保存在 JSON 文件（见 PanSourceCollectSiteStore），新增/修改/删除均直接落盘。
    /// 字段校验统一由 <see cref="PanSourceCollectSiteValidator"/> 负责。
    /// </summary>
    public class PanSourceCollectSiteBLL
    {
        #region 获取数据

        public TData<List<PanSourceCollectSite>> GetList()
        {
            TData<List<PanSourceCollectSite>> obj = new TData<List<PanSourceCollectSite>>();
            obj.Data = PanSourceCollectSiteStore.GetAll();
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        #endregion

        #region 提交数据

        /// <summary>新增或修改站点：Id 为空视为新增，否则按 Id 覆盖。</summary>
        public TData<string> SaveForm(PanSourceCollectSite entity)
        {
            TData<string> obj = new TData<string>();
            if (entity == null)
            {
                obj.Message = "参数不能为空";
                return obj;
            }

            entity.SiteName = (entity.SiteName ?? string.Empty).Trim();
            entity.SiteUrl = (entity.SiteUrl ?? string.Empty).Trim();
            entity.Id = (entity.Id ?? string.Empty).Trim();

            if (!PanSourceCollectSiteValidator.Validate(entity, out string message))
            {
                obj.Message = message;
                return obj;
            }

            // 站点编辑表单只提交名称与链接，未携带分类配置时继承已保存的映射，避免编辑站点把配置清空
            if (!PanSourceCollectSiteValidator.NormalizeCategories(entity.Categories, out List<PanSourceCollectSiteCategory> categories, out string categoryMessage))
            {
                obj.Message = categoryMessage;
                return obj;
            }
            entity.Categories = categories;

            DateTime now = DateTime.Now;
            bool isAdd = entity.Id.Length == 0;
            if (isAdd)
            {
                entity.Id = Guid.NewGuid().ToString("N");
                entity.CreateTime = now;
            }
            else
            {
                PanSourceCollectSite exist = PanSourceCollectSiteStore.GetById(entity.Id);
                if (exist == null)
                {
                    obj.Message = "站点不存在或已被删除";
                    return obj;
                }
                entity.CreateTime = exist.CreateTime;
                if (entity.Categories.Count == 0 && exist.Categories != null && exist.Categories.Count > 0)
                {
                    entity.Categories = exist.Categories;
                }
            }
            entity.UpdateTime = now;

            PanSourceCollectSiteStore.Save(entity);

            obj.Data = entity.Id;
            obj.Tag = 1;
            obj.Message = isAdd ? "站点添加成功" : "站点保存成功";
            return obj;
        }

        public TData Delete(string id)
        {
            TData obj = new TData();
            if (string.IsNullOrWhiteSpace(id))
            {
                obj.Message = "请选择要删除的站点";
                return obj;
            }
            if (!PanSourceCollectSiteStore.Delete(id))
            {
                obj.Message = "站点不存在或已被删除";
                return obj;
            }
            obj.Tag = 1;
            obj.Message = "删除成功";
            return obj;
        }

        #endregion
    }
}
