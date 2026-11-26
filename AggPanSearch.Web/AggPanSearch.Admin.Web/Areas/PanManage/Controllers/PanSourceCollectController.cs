using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Business.PanManage.Quark;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：采集资源控制器（pan_source_collect）。
    /// 后台「网盘管理 - 采集资源」维护采集到的资源条目及链接有效性。
    /// </summary>
    [Area("PanManage")]
    public class PanSourceCollectController : BaseController
    {
        private PanSourceCollectBLL panSourceCollectBLL = new PanSourceCollectBLL();
        private PanSourceCollectSiteBLL panSourceCollectSiteBLL = new PanSourceCollectSiteBLL();
        private PanSourceCollectCategoryBLL panSourceCollectCategoryBLL = new PanSourceCollectCategoryBLL();

        #region 视图功能

        [AuthorizeFilter("pan:pansourcecollect:view")]
        public ActionResult PanSourceCollectIndex()
        {
            return View();
        }

        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public ActionResult PanSourceCollectForm()
        {
            return View();
        }

        /// <summary>采集参数弹窗页：三种采集方式以 tab 切换，地址等参数在页面内手动填写。</summary>
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult PanSourceCollectParamForm()
        {
            return View();
        }

        /// <summary>配置分类弹窗页：维护站点分类与「分类资源」的对应关系（采集前必须先配置）。</summary>
        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public ActionResult PanSourceCollectCategoryForm()
        {
            return View();
        }

        #endregion

        #region 获取数据

        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:search")]
        public async Task<ActionResult> GetPageListJson(PanSourceCollectListParam param, Pagination pagination)
        {
            TData<List<PanSourceCollectEntity>> obj = await panSourceCollectBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:view")]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanSourceCollectEntity> obj = await panSourceCollectBLL.GetEntity(id);
            return Json(obj);
        }

        /// <summary>采集站点列表（站点信息保存于 JSON 文件，含已配置的分类映射）。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:view")]
        public ActionResult GetSiteListJson()
        {
            TData<List<PanSourceCollectSite>> obj = panSourceCollectSiteBLL.GetList();
            return Json(obj);
        }

        /// <summary>配置分类弹窗的「分类资源」下拉数据（只返回下拉需要的字段）。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:view")]
        public async Task<ActionResult> GetSourceCategoryOptionJson()
        {
            TData<List<PanSourceCategoryOption>> obj = new TData<List<PanSourceCategoryOption>>();
            TData<List<PanSourceCategoryEntity>> result = await panSourceCollectCategoryBLL.GetSourceCategoryList();
            if (result.Tag != 1)
            {
                obj.Message = result.Message;
                return Json(obj);
            }

            obj.Data = (result.Data ?? new List<PanSourceCategoryEntity>())
                .Where(t => t.Id.HasValue)
                .OrderBy(t => t.Sort ?? 0)
                .ThenBy(t => t.Id.Value)
                .Select(t => new PanSourceCategoryOption
                {
                    Id = t.Id.Value,
                    Name = (t.Name ?? string.Empty).Trim(),
                    Status = t.Status ?? 1,
                })
                .ToList();
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        /// <summary>抓取站点分类候选（自动抓取站点首页导航中的分类入口）。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:view")]
        public async Task<ActionResult> FetchSiteCategoriesJson(string siteId)
        {
            TData<List<SiteCategoryCandidate>> obj = await panSourceCollectCategoryBLL.FetchSiteCategories(siteId);
            return Json(obj);
        }

        #endregion

        #region 提交数据

        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public async Task<ActionResult> SaveFormJson(PanSourceCollectEntity entity)
        {
            TData<string> obj = await panSourceCollectBLL.SaveForm(entity);
            return Json(obj);
        }

        /// <summary>
        /// 按站点 + 采集模式启动任务（后台执行）。
        /// mode：0 详情页采集 / 1 单个列表页采集 / 2 分页列表采集。
        /// detailUrl：详情页地址，仅模式 0 生效。
        /// listUrl：列表页地址，模式 1 作为入口地址；模式 2 作为起始列表页地址，按其页码开始逐页采集。
        /// pageTemplate：分页链接模板（含 {page}），仅模式 2 生效。
        /// category：历史兼容参数（采集参数页已去掉分类输入，前端不再提交），
        /// 语义为「本次采集统一使用的站点分类名」，服务端按站点「配置分类」映射成分类资源 Id，留空则逐条按解析出的分类名匹配。
        /// maxPages：本次采集的页数上限（1-100），0 或非法值使用引擎默认值，模式 0 忽略。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult StartCollectJson(
            string siteId,
            int mode = 1,
            string detailUrl = "",
            string category = "",
            int maxPages = 0,
            string listUrl = "",
            string pageTemplate = "")
        {
            TData<string> obj = panSourceCollectBLL.StartCollect(siteId, mode, detailUrl, category, maxPages, listUrl, pageTemplate);
            return Json(obj);
        }

        /// <summary>新增或修改采集站点（Id 为空视为新增；未提交分类配置时保留原有映射）。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public ActionResult SaveSiteJson(PanSourceCollectSite entity)
        {
            TData<string> obj = panSourceCollectSiteBLL.SaveForm(entity);
            return Json(obj);
        }

        /// <summary>
        /// 保存站点分类配置（站点分类 ↔ 分类资源 的对应关系）。
        /// categories 为 JSON 数组字符串：[{Name,Url,SourceCategoryId}]。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public async Task<ActionResult> SaveSiteCategoriesJson(string siteId, string categories)
        {
            List<PanSourceCollectSiteCategory> list;
            try
            {
                list = JsonConvert.DeserializeObject<List<PanSourceCollectSiteCategory>>(categories ?? string.Empty)
                    ?? new List<PanSourceCollectSiteCategory>();
            }
            catch (JsonException)
            {
                TData<int> error = new TData<int> { Message = "分类配置数据格式不正确，请刷新页面后重试" };
                return Json(error);
            }

            TData<int> obj = await panSourceCollectCategoryBLL.SaveCategories(siteId, list);
            return Json(obj);
        }

        /// <summary>删除采集站点。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:delete")]
        public ActionResult DeleteSiteJson(string id)
        {
            TData obj = panSourceCollectSiteBLL.Delete(id);
            return Json(obj);
        }

        /// <summary>
        /// 把选中的采集资源转存到本人夸克网盘「采集资源」目录，并生成永久无提取码的分享链接写入本地资源。
        /// 后台执行，返回 jobId，前端通过 GetTransferProgressJson 轮询进度。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public async Task<ActionResult> TransferToLocalJson(string ids)
        {
            TData<string> obj = await panSourceCollectBLL.StartTransfer(ids);
            return Json(obj);
        }

        /// <summary>查询转存任务进度；jobId 为空或已过期时返回最近运行中的任务。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult GetTransferProgressJson(string jobId)
        {
            TData<QuarkPanTransferState> obj = panSourceCollectBLL.GetTransferProgress(jobId);
            return Json(obj);
        }

        /// <summary>取消正在运行的转存任务。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult CancelTransferJson(string jobId)
        {
            TData obj = panSourceCollectBLL.CancelTransfer(jobId);
            return Json(obj);
        }

        /// <summary>查询采集任务进度；jobId 为空或已过期时返回最近运行中的任务。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult GetCollectProgressJson(string jobId)
        {
            TData<PanSourceCollectJobState> obj = panSourceCollectBLL.GetCollectProgress(jobId);
            return Json(obj);
        }

        /// <summary>取消正在运行的采集任务。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult CancelCollectJson(string jobId)
        {
            TData obj = panSourceCollectBLL.CancelCollect(jobId);
            return Json(obj);
        }

        /// <summary>
        /// 启动补图任务：把原图片地址下载到本机并回写本地图片地址。
        /// ids 为空时补缺少本地图片的前 limit 条记录；后台执行，返回 jobId，前端通过 GetImageProgressJson 轮询。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add,pan:pansourcecollect:edit")]
        public ActionResult BackfillImageJson(string ids, int limit = 0)
        {
            TData<string> obj = panSourceCollectBLL.StartBackfillImage(ids, limit);
            return Json(obj);
        }

        /// <summary>查询补图任务进度；jobId 为空或已过期时返回最近运行中的任务。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult GetImageProgressJson(string jobId)
        {
            TData<PanSourceCollectImageJobState> obj = panSourceCollectBLL.GetImageProgress(jobId);
            return Json(obj);
        }

        /// <summary>查询自动补图队列进度（采集入库后自动下载本地图片的实时状态，页面加载即开始轮询）。</summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansourcecollect:view")]
        public ActionResult GetAutoBackfillProgressJson()
        {
            TData<PanSourceCollectAutoQueueState> obj = panSourceCollectBLL.GetAutoBackfillProgress();
            return Json(obj);
        }

        /// <summary>取消正在运行的补图任务（已回写的本地图片保留）。</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:add")]
        public ActionResult CancelBackfillImageJson(string jobId)
        {
            TData obj = panSourceCollectBLL.CancelBackfillImage(jobId);
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansourcecollect:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panSourceCollectBLL.DeleteForm(ids);
            return Json(obj);
        }

        #endregion
    }

    /// <summary>配置分类弹窗的「分类资源」下拉项（只暴露下拉需要的字段）。</summary>
    public class PanSourceCategoryOption
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>1 显示 / 0 不显示，用于下拉分组提示。</summary>
        public int Status { get; set; }
    }
}
