using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：TG频道维护控制器（pan_search_channel）。
    /// 取代原先在「搜索设置 - TG频道」里用一个 textarea 维护全部频道的方式：
    /// 现在每个频道一条记录，可单独启停、分组、排序，前台 /api/other/web_search 只读取 status=1 的频道。
    /// </summary>
    [Area("PanManage")]
    public class PanSearchChannelController : BaseController
    {
        private PanSearchChannelBLL panSearchChannelBLL = new PanSearchChannelBLL();

        #region 视图功能
        [AuthorizeFilter("pan:pansearchchannel:view")]
        public ActionResult PanSearchChannelIndex()
        {
            return View();
        }

        public ActionResult PanSearchChannelForm()
        {
            return View();
        }
        #endregion

        #region 获取数据
        [HttpGet]
        [AuthorizeFilter("pan:pansearchchannel:search")]
        public async Task<ActionResult> GetListJson(PanSearchChannelListParam param)
        {
            TData<List<PanSearchChannelEntity>> obj = await panSearchChannelBLL.GetList(param);
            return Json(obj);
        }

        [HttpGet]
        [AuthorizeFilter("pan:pansearchchannel:search")]
        public async Task<ActionResult> GetPageListJson(PanSearchChannelListParam param, Pagination pagination)
        {
            TData<List<PanSearchChannelEntity>> obj = await panSearchChannelBLL.GetPageList(param, pagination);
            return Json(obj);
        }

        [HttpGet]
        public async Task<ActionResult> GetFormJson(long id)
        {
            TData<PanSearchChannelEntity> obj = await panSearchChannelBLL.GetEntity(id);
            return Json(obj);
        }
        #endregion

        #region 提交数据
        [HttpPost]
        [AuthorizeFilter("pan:pansearchchannel:add,pan:pansearchchannel:edit")]
        public async Task<ActionResult> SaveFormJson(PanSearchChannelEntity entity)
        {
            TData<string> obj = await panSearchChannelBLL.SaveForm(entity);
            if (obj.Tag == 1)
            {
                // 频道变更后立即让前台搜索的频道缓存失效，无需等 60 秒
                UserFrontendApiController.ClearSearchChannelCache();
            }
            return Json(obj);
        }

        [HttpPost]
        [AuthorizeFilter("pan:pansearchchannel:delete")]
        public async Task<ActionResult> DeleteFormJson(string ids)
        {
            TData obj = await panSearchChannelBLL.DeleteForm(ids);
            if (obj.Tag == 1)
            {
                UserFrontendApiController.ClearSearchChannelCache();
            }
            return Json(obj);
        }

        /// <summary>批量启用/停用：status=1 启用，0 停用</summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansearchchannel:edit")]
        public async Task<ActionResult> SetStatusJson(string ids, int status)
        {
            TData obj = await panSearchChannelBLL.SetStatus(ids, status);
            if (obj.Tag == 1)
            {
                UserFrontendApiController.ClearSearchChannelCache();
            }
            return Json(obj);
        }

        /// <summary>
        /// 重置系统默认频道：清空表内全部数据后，重新写入 AggPanSearch 内置默认频道（全部启用）
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansearchchannel:add")]
        public async Task<ActionResult> ResetDefaultJson()
        {
            List<string> names = new AggPanSearch.Config.AggPanSearchConfig().GetChannelList().ToList();
            TData<int> obj = await panSearchChannelBLL.ResetDefaults(names);
            if (obj.Tag == 1)
            {
                UserFrontendApiController.ClearSearchChannelCache();
            }
            return Json(obj);
        }

        /// <summary>
        /// 检测单个频道：是否可访问 + 最近一次发布网盘资源链接的时间（结果回写 last_update）
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansearchchannel:search")]
        public async Task<ActionResult> CheckChannelJson(long id)
        {
            TData<AggPanSearch.Sources.TgChannelProbeResult> obj = new TData<AggPanSearch.Sources.TgChannelProbeResult>();
            TData<PanSearchChannelEntity> entityObj = await panSearchChannelBLL.GetEntity(id);
            if (entityObj.Tag != 1 || entityObj.Data == null)
            {
                obj.Message = "频道不存在";
                return Json(obj);
            }

            // 代理与前台搜索保持一致（appsettings.json 的 AggPanSearch:Proxy）
            AggPanSearch.Config.AggPanSearchConfig config = new AggPanSearch.Config.AggPanSearchConfig
            {
                Proxy = GetAggPanSearchProxy()
            };
            AggPanSearch.Sources.TgChannelProbeResult probe =
                await AggPanSearch.Sources.TgChannelProbe.ProbeAsync(entityObj.Data.Name, config);
            // 只有得出确定结论（可访问 / 确定不可访问）才回写，网络异常、超时、限流等未确定情况不写库，
            // 避免把本来正常的频道错误标成「不可访问」
            if (probe.Outcome != AggPanSearch.Sources.TgProbeOutcome.Unknown)
            {
                await panSearchChannelBLL.SaveProbeResult(id, probe.Accessible, probe.LastUpdate);
            }

            obj.Data = probe;
            obj.Tag = 1;
            obj.Message = probe.Message;
            return Json(obj);
        }

        private string GetAggPanSearchProxy()
        {
            object service = HttpContext?.RequestServices?.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration));
            Microsoft.Extensions.Configuration.IConfiguration configuration =
                service as Microsoft.Extensions.Configuration.IConfiguration;
            string proxy = configuration?["AggPanSearch:Proxy"];
            return string.IsNullOrWhiteSpace(proxy) ? null : proxy.Trim();
        }
        #endregion
    }
}
