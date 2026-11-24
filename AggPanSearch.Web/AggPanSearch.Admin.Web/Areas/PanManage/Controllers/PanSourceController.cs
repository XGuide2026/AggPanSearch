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
    /// 日 期：2026-11-04 03:11
    /// 描 述：控制器类
    /// </summary>
    [Area("PanManage")]
    public class PanSourceController :  BaseController
    {
        private PanSourceBLL panSourceBLL = new PanSourceBLL();
        private PanSourceAiBLL panSourceAiBLL = new PanSourceAiBLL();

        #region 视图功能
        [AuthorizeFilter("pan:pansource:view")]
        public ActionResult PanSourceIndex()
        {
            return View();
        }

        public ActionResult PanSourceForm()
        {
            return View();
        }

        /// <summary>资源介绍独立弹窗（含读链接、AI 改写）</summary>
        public ActionResult PanSourceIntroForm()
        {
            return View();
        }

        /// <summary>资源图片生成弹窗（图片生成模式 + 弹窗内暂存/保存）</summary>
        public ActionResult PanSourceImageForm()
        {
            return View();
        }
        #endregion

        #region 获取数据
        [HttpGet]
        [AuthorizeFilter("pan:pansource:search")]
        public async Task<ActionResult> GetListJson(PanSourceListParam param)
        {
            TData<List<PanSourceEntity>> obj = await panSourceBLL.GetList(param);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pansource:search")]
        public async Task<ActionResult> GetPageListJson(PanSourceListParam param, Pagination pagination)
        {
            TData<List<PanSourceEntity>> obj = await panSourceBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanSourceEntity> obj = await panSourceBLL.GetEntity(id);
            return Json(obj);
        }

        /// <summary>
        /// 仅读取夸克分享链接的事实清单（不使用 AI、不消耗 token）。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansource:edit")]
        public async Task<ActionResult> GetShareFactJson(long id, string code)
        {
            TData<ShareFact> obj = await panSourceAiBLL.BuildFact(id, code);
            return Json(obj);
        }

        /// <summary>
        /// 基于链接事实调用 AI 生成资源介绍，只返回文本，不落库。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansource:edit")]
        public async Task<ActionResult> AiRewriteIntroJson(long id, string code)
        {
            TData<AiRewriteResult> obj = await panSourceAiBLL.Rewrite(id, code);
            return Json(obj);
        }
        #endregion

        #region 提交数据
        [HttpPost]
        [AuthorizeFilter("pan:pansource:add,pan:pansource:edit")]
        public async Task<ActionResult> SaveFormJson(PanSourceEntity entity)
        {
            TData<string> obj = await panSourceBLL.SaveForm(entity);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansource:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panSourceBLL.DeleteForm(ids);
            return Json(obj);
        }
        #endregion

        #region 自动截图
        /// <summary>
        /// 启动截图任务：对选中的夸克资源用无头浏览器截图，图片落盘后追加到 vod_pic 末尾。
        /// 后台执行，返回 jobId，前端通过 GetScreenshotProgressJson 轮询进度。
        /// mode 为图片生成模式（1=打开文件夹显示文件列表截图，预留扩展）；persist 为 true 时直接写库，
        /// 为 false（资源图片生成弹窗）时仅落盘并在任务状态中回传图片地址，不写库，由弹窗统一保存。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansource:edit")]
        public ActionResult StartScreenshotJson(string ids, int mode = 1, bool persist = true)
        {
            TData<string> obj = panSourceBLL.StartScreenshot(ids, mode, persist);
            return Json(obj);
        }

        /// <summary>查询截图任务进度；jobId 为空或已过期时返回最近运行中的任务。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansource:edit")]
        public ActionResult GetScreenshotProgressJson(string jobId)
        {
            TData<PanSourceScreenshotJobState> obj = panSourceBLL.GetScreenshotProgress(jobId);
            return Json(obj);
        }

        /// <summary>取消正在运行的截图任务（已保存的截图保留）。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansource:edit")]
        public ActionResult CancelScreenshotJson(string jobId)
        {
            TData obj = panSourceBLL.CancelScreenshot(jobId);
            return Json(obj);
        }
        #endregion
    }
}
