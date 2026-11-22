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
}
