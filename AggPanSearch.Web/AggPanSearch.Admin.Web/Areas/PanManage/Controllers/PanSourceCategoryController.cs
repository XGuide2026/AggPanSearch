using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;
using AggPanSearch.Entity;
using AggPanSearch.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 02:05
    /// 描 述：控制器类
    /// </summary>
    [Area("PanManage")]
    public class PanSourceCategoryController :  BaseController
    {
        private PanSourceCategoryBLL panSourceCategoryBLL = new PanSourceCategoryBLL();

        #region 视图功能
        [AuthorizeFilter("pan:pansourcecategory:view")]
        public ActionResult PanSourceCategoryIndex()
        {
            return View();
        }

        public ActionResult PanSourceCategoryForm()
        {
            return View();
        }
        #endregion

        #region 获取数据
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecategory:search")]
        public async Task<ActionResult> GetListJson(PanSourceCategoryListParam param)
        {
            TData<List<PanSourceCategoryEntity>> obj = await panSourceCategoryBLL.GetList(param);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pansourcecategory:search")]
        public async Task<ActionResult> GetPageListJson(PanSourceCategoryListParam param, Pagination pagination)
        {
            TData<List<PanSourceCategoryEntity>> obj = await panSourceCategoryBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        /// <summary>
        /// 资源分类导航：分类列表 + 每个分类的资源总数，供本地资源页左侧侧栏（及卡片分类标签）一次请求使用。
        /// 排序与过滤规则与前台 GetCategories 保持一致：空名称/禁用分类剔除，Sort、Id 升序；
        /// 数量统计排除软删除与禁用资源，且仅当分类 Id ≤ int.MaxValue 时匹配（pan_source.SourceCategoryId 为 int 列）。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecategory:search")]
        public async Task<ActionResult> GetCategoryNavJson()
        {
            TData<List<CategoryNavItem>> obj = new TData<List<CategoryNavItem>>();
            var list = new List<CategoryNavItem>();
            try
            {
                var categoryObj = await panSourceCategoryBLL.GetList(null);
                if (categoryObj != null && categoryObj.Tag == 1 && categoryObj.Data != null)
                {
                    list = categoryObj.Data
                        .Where(c => c.Id.HasValue && !string.IsNullOrWhiteSpace(c.Name))
                        .Where(c => c.Status == null || c.Status != 0)
                        .OrderBy(c => c.Sort ?? 0)
                        .ThenBy(c => c.Id)
                        .Select(c => new CategoryNavItem
                        {
                            Id = c.Id.Value,
                            Name = c.Name
                        })
                        .ToList();
                }
            }
            catch
            {
                // 分类表不存在或数据库不可用时返回空集合，页面只显示"全部资源"
            }

            if (list.Count > 0)
            {
                var counts = await new PanSourceBLL().GetCategoryCounts();
                if (counts != null && counts.Count > 0)
                {
                    foreach (var item in list)
                    {
                        if (item.Id > 0 && item.Id <= int.MaxValue
                            && counts.TryGetValue((int)item.Id, out int count))
                        {
                            item.Count = count;
                        }
                    }
                }
            }

            // 「未分类」导航项：SourceCategoryId 为空或 0 的资源（排除软删除与禁用），置于列表最前
            int uncategorizedCount = await new PanSourceBLL().GetUncategorizedCount();
            list.Insert(0, new CategoryNavItem { Id = 0, Name = "未分类", Count = uncategorizedCount });

            obj.Data = list;
            obj.Total = list.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        [HttpGet]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanSourceCategoryEntity> obj = await panSourceCategoryBLL.GetEntity(id);
            return Json(obj);
        }
        #endregion

        #region 提交数据
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecategory:add,pan:pansourcecategory:edit")]
        public async Task<ActionResult> SaveFormJson(PanSourceCategoryEntity entity)
        {
            TData<string> obj = await panSourceCategoryBLL.SaveForm(entity);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansourcecategory:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panSourceCategoryBLL.DeleteForm(ids);
            return Json(obj);
        }
        #endregion
    }

    /// <summary>本地资源页左侧"资源分类"导航项（分类 + 资源总数）。</summary>
    public class CategoryNavItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>该分类下的资源总数。</summary>
        public int Count { get; set; }
    }
}
