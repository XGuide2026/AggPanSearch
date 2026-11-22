using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Util.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：搜索日志控制器（pan_search_log）
    /// </summary>
    [Area("PanManage")]
    public class PanSearchLogController : BaseController
    {
        private PanSearchLogBLL panSearchLogBLL = new PanSearchLogBLL();

        #region 视图功能

        [AuthorizeFilter("pan:pansearchlog:view")]
        public ActionResult PanSearchLogIndex()
        {
            return View();
        }

        public ActionResult PanSearchLogForm()
        {
            return View();
        }

        #endregion

        #region 获取数据

        [HttpGet]
        [AuthorizeFilter("pan:pansearchlog:search")]
        public async Task<ActionResult> GetListJson(PanSearchLogListParam param)
        {
            TData<List<PanSearchLogEntity>> obj = await panSearchLogBLL.GetList(param);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pansearchlog:search")]
        public async Task<ActionResult> GetPageListJson(PanSearchLogListParam param, Pagination pagination)
        {
            TData<List<PanSearchLogEntity>> obj = await panSearchLogBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanSearchLogEntity> obj = await panSearchLogBLL.GetEntity(id);
            return Json(obj);
        }

        /// <summary>
        /// 今日搜索量 / 总搜索量
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansearchlog:search")]
        public async Task<ActionResult> GetSearchCountJson()
        {
            TData<int> todayObj = await panSearchLogBLL.GetTodayCount();
            TData<int> totalObj = await panSearchLogBLL.GetTotalCount();
            TData<object> obj = new TData<object>();
            obj.Data = new { today = todayObj.Data, total = totalObj.Data };
            obj.Tag = 1;
            return Json(obj);
        }

        #endregion

        #region 提交数据

        [HttpPost]
        [AuthorizeFilter("pan:pansearchlog:add,pan:pansearchlog:edit")]
        public async Task<ActionResult> SaveFormJson(PanSearchLogEntity entity)
        {
            TData<string> obj = await panSearchLogBLL.SaveForm(entity);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansearchlog:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panSearchLogBLL.DeleteForm(ids);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansearchlog:delete")]
        public async Task<ActionResult> ClearFormJson()
        {
            TData obj = await panSearchLogBLL.ClearForm();
            return Json(obj);
        }

        #endregion
    }
}
