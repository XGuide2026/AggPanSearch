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
    /// 日 期：2026-11-03 03:01
    /// 描 述：控制器类
    /// </summary>
    [Area("PanManage")]
    public class PanConfController :  BaseController
    {
        private PanConfBLL panConfBLL = new PanConfBLL();

        #region 视图功能
        [AuthorizeFilter("pan:panconf:view")]
        public ActionResult PanConfIndex()
        {
            return View();
        }

        /// <summary>
        /// 参数配置列表页（pan_conf 增删改查）
        /// </summary>
        [AuthorizeFilter("pan:panconf:view")]
        public ActionResult PanConfList()
        {
            return View();
        }

        [AuthorizeFilter("pan:panconf:add,pan:panconf:edit")]
        public ActionResult PanConfForm()
        {
            return View();
        }
        #endregion

        #region 获取数据
        [HttpGet]
        [AuthorizeFilter("pan:panconf:search")]
        public async Task<ActionResult> GetListJson(PanConfListParam param)
        {
            TData<List<PanConfEntity>> obj = await panConfBLL.GetList(param);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:panconf:search")]
        public async Task<ActionResult> GetPageListJson(PanConfListParam param, Pagination pagination)
        {
            TData<List<PanConfEntity>> obj = await panConfBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanConfEntity> obj = await panConfBLL.GetEntity(id);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:panconf:search")]
        public async Task<ActionResult> GetConfTypeListJson()
        {
            TData<List<int?>> obj = await panConfBLL.GetConfTypeList();
            return Json(obj);
        }
        #endregion

        #region 提交数据
        [HttpPost]
        [AuthorizeFilter("pan:panconf:add,pan:panconf:edit")]
        public async Task<ActionResult> SaveFormJson(PanConfEntity entity)
        {
            TData<string> obj = await panConfBLL.SaveForm(entity);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:panconf:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panConfBLL.DeleteForm(ids);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:panconf:add,pan:panconf:edit")]
        public async Task<ActionResult> SaveBatchJson([FromBody] List<PanConfEntity> entities)
        {
            TData obj = await panConfBLL.SaveBatch(entities);
            return Json(obj);
        }
        #endregion
    }
}
