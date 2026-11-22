using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Util.Model;
using AggPanSearch.Web.Code;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：版权投诉审核控制器（pan_copyright_complaint）。
    /// 前台「版权声明 · 侵权投诉」提交的投诉在此查看与审核；
    /// 审核通过后作品名写入 pan_conf.copyright_block_keywords，前台搜索结果即时过滤。
    /// </summary>
    [Area("PanManage")]
    public class PanCopyrightComplaintController : BaseController
    {
        private PanCopyrightComplaintBLL panCopyrightComplaintBLL = new PanCopyrightComplaintBLL();

        #region 视图功能

        [AuthorizeFilter("pan:pancopyrightcomplaint:view")]
        public ActionResult PanCopyrightComplaintIndex()
        {
            return View();
        }

        public ActionResult PanCopyrightComplaintForm()
        {
            return View();
        }

        #endregion

        #region 获取数据

        [HttpGet]
        [AuthorizeFilter("pan:pancopyrightcomplaint:search")]
        public async Task<ActionResult> GetPageListJson(PanCopyrightComplaintListParam param, Pagination pagination)
        {
            TData<List<PanCopyrightComplaintEntity>> obj = await panCopyrightComplaintBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pancopyrightcomplaint:view")]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanCopyrightComplaintEntity> obj = await panCopyrightComplaintBLL.GetEntity(id);
            return Json(obj);
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 批量审核：status=1 通过，status=2 驳回，remark 必填。
        /// 审核后立即让搜索配置缓存失效，使版权关键词马上生效。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pancopyrightcomplaint:edit")]
        public async Task<ActionResult> AuditJson(string ids, int status, string remark)
        {
            OperatorInfo user = await Operator.Instance.Current();
            TData obj = await panCopyrightComplaintBLL.Audit(ids, status, remark, user?.UserId ?? 0);
            if (obj.Tag == 1)
            {
                UserFrontendApiController.ClearSearchConfCache();
            }
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pancopyrightcomplaint:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panCopyrightComplaintBLL.DeleteForm(ids);
            if (obj.Tag == 1)
            {
                UserFrontendApiController.ClearSearchConfCache();
            }
            return Json(obj);
        }

        #endregion
    }
}
