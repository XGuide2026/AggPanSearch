using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Business.PanManage;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：AI设置控制器。
    /// 从「网盘管理 - 基础设置」中拆分出来的独立页面：原 conf_type=14 的 AI 配置项。
    /// </summary>
    [Area("PanManage")]
    public class PanAiConfController : BaseController
    {
        private PanConfBLL panConfBLL = new PanConfBLL();

        #region 视图功能

        [AuthorizeFilter("pan:panaiconf:view")]
        public ActionResult PanAiConfIndex()
        {
            return View();
        }

        #endregion

        #region 获取数据

        /// <summary>
        /// 「AI设置」页：返回归类到 AI 的配置项（conf_type=14 或 ConfKey 以 ai_ 开头）。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:panaiconf:search")]
        public async Task<ActionResult> GetListJson()
        {
            TData<List<PanConfEntity>> obj = await panConfBLL.GetList(null);
            List<PanConfEntity> aiList = (obj.Data ?? new List<PanConfEntity>())
                .Where(t => IsAiConf(t))
                .OrderBy(t => t.ConfSort ?? 0)
                .ThenBy(t => t.Id)
                .ToList();
            obj.Data = aiList;
            obj.Total = aiList.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 保存「AI设置」页的配置项（按 Id 批量更新 conf_value）。
        /// AI 业务每次实时读库（PanSourceAiBLL 经 panConfBLL.GetList 读取），保存即时生效，无需清缓存。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:panaiconf:edit")]
        public async Task<ActionResult> SaveBatchJson([FromBody] List<PanConfEntity> entities)
        {
            TData obj = await panConfBLL.SaveBatch(entities);
            if (obj.Tag == 1)
            {
                obj.Message = "保存成功";
            }
            return Json(obj);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 是否 AI 设置项：conf_type=14 或 ConfKey 以 ai_ 开头（历史数据可能归在其它分类下）。
        /// 注意：与 PanConfIndex.cshtml 中的 isAiConf 保持一致，否则两边会重复或漏项。
        /// </summary>
        private static bool IsAiConf(PanConfEntity entity)
        {
            if (entity == null)
            {
                return false;
            }
            if ((entity.ConfType ?? 0) == 14)
            {
                return true;
            }
            return !string.IsNullOrEmpty(entity.ConfKey)
                && entity.ConfKey.StartsWith("ai_", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}