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
    /// 描 述：搜索设置控制器。
    /// 从「系统配置」中拆分出来的独立页面：原 conf_type=1 的搜索项 + 屏蔽词 + 搜索插件 + 网盘类型。
    /// </summary>
    [Area("PanManage")]
    public class PanSearchConfController : BaseController
    {
        private PanConfBLL panConfBLL = new PanConfBLL();
        private readonly AggPanSearch.AggPanSearchService aggPanSearchService;

        public PanSearchConfController(AggPanSearch.AggPanSearchService aggPanSearchService)
        {
            this.aggPanSearchService = aggPanSearchService;
        }

        /// <summary>
        /// 除 conf_type=1 外，以下 conf_key 也属于搜索设置（历史数据可能归在其它分类下）。
        /// 注意：与 PanConfIndex.cshtml 中的 SEARCH_CONF_KEYS 保持一致，否则两边会重复或漏项。
        /// </summary>
        private static readonly string[] SearchConfKeys = { "search_tips", "search_bg", "pc_type", "is_quan" };

        /// <summary>屏蔽词过滤</summary>
        private const string BlockWordsKey = "search_block_words";
        /// <summary>搜索插件（每行一个插件名，留空则使用全部已注册插件）</summary>
        private const string PluginsKey = "search_plugins";
        /// <summary>网盘类型（逗号分隔的 AggPanSearch 类型名，留空则返回全部类型）</summary>
        private const string CloudTypesKey = "search_cloud_types";
        /// <summary>
        /// 版权屏蔽关键词：由「网盘管理 - 版权投诉」审核通过后自动重建（只含 status=1 的作品名），
        /// 与人工维护的 search_block_words 分库存储，互不覆盖。
        /// </summary>
        private static readonly string CopyrightBlockWordsKey = PanCopyrightComplaintBLL.KeywordsConfKey;

        /// <summary>
        /// 独立 Tab 维护的配置项 key：这些项不在「搜索设置」表单里重复出现。
        /// 注意：
        /// 1. TG 频道已改为 pan_search_channel 表维护（「网盘管理 - TG频道」），不再走 pan_conf；
        /// 2. 版权屏蔽关键词在「屏蔽词过滤」Tab 中只读展示，SaveExtraJson 不会写入它。
        /// </summary>
        private static readonly string[] ExtraKeys = { BlockWordsKey, PluginsKey, CloudTypesKey, CopyrightBlockWordsKey };

        /// <summary>
        /// 已下线功能的遗留配置 key：库里可能还留着旧记录（conf_type=1），
        /// 列表与表单里一律忽略，避免已删除的「搜索热词」重新出现在页面上。
        /// </summary>
        private static readonly string[] RetiredKeys = { "search_hot_words" };

        #region 视图功能

        [AuthorizeFilter("pan:pansearchconf:view")]
        public ActionResult PanSearchConfIndex()
        {
            return View();
        }

        #endregion

        #region 获取数据

        /// <summary>
        /// 「搜索设置」Tab：返回归类到搜索的配置项（conf_type=1 或命中 SearchConfKeys）。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansearchconf:search")]
        public async Task<ActionResult> GetListJson()
        {
            TData<List<PanConfEntity>> obj = await panConfBLL.GetList(null);
            List<PanConfEntity> searchList = (obj.Data ?? new List<PanConfEntity>())
                .Where(t => IsSearchConf(t) && !IsExtraConf(t) && !IsRetiredConf(t))
                .OrderBy(t => t.ConfSort ?? 0)
                .ThenBy(t => t.Id)
                .ToList();
            obj.Data = searchList;
            obj.Total = searchList.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        /// <summary>
        /// 独立 Tab（屏蔽词 / 搜索插件 / 网盘类型）：返回对应配置值（不存在时返回空字符串）。
        /// TG 频道不再返回，改由「网盘管理 - TG频道」页面维护 pan_search_channel 表。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansearchconf:search")]
        public async Task<ActionResult> GetExtraJson()
        {
            TData<Dictionary<string, string>> obj = await panConfBLL.GetConfValues(ExtraKeys.ToList());
            return Json(obj);
        }

        /// <summary>
        /// 「搜索插件」Tab：返回 AggPanSearch 运行时已注册的插件名（供页面提示可填写的插件）。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansearchconf:search")]
        public ActionResult GetPluginListJson()
        {
            TData<List<string>> obj = new TData<List<string>>();
            try
            {
                obj.Data = aggPanSearchService.Plugins.All
                    .Select(t => t.Name)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .OrderBy(t => t)
                    .ToList();
            }
            catch
            {
                obj.Data = new List<string>();
            }
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        /// <summary>
        /// 「网盘类型」Tab：返回全部网盘类型（与前台共用 PanCloudTypeRegistry，页面不再硬编码）
        /// 以及当前启用的类型（pan_conf.search_cloud_types）。
        /// enabled 为空表示不过滤，等同全部启用，保持原有默认行为。
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pansearchconf:search")]
        public async Task<ActionResult> GetCloudTypeListJson()
        {
            TData<object> obj = new TData<object>();
            List<string> enabled = new List<string>();
            try
            {
                TData<Dictionary<string, string>> confObj = await panConfBLL.GetConfValues(new List<string> { CloudTypesKey });
                string saved = "";
                if (confObj != null && confObj.Data != null && confObj.Data.ContainsKey(CloudTypesKey))
                {
                    saved = confObj.Data[CloudTypesKey] ?? "";
                }
                enabled = saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0 && PanCloudTypeRegistry.Find(t) != null)
                    .Distinct()
                    .ToList();
            }
            catch
            {
                // 配置读取失败不影响页面渲染，按「未配置=全部启用」处理
            }

            obj.Data = new
            {
                // useAll：未配置启用项，前端据此把全部类型显示为已勾选
                useAll = enabled.Count == 0,
                enabled = enabled,
                types = PanCloudTypeRegistry.All
                    .Select(t => new { value = t.Value, text = t.Text, isType = t.IsType })
                    .ToList()
            };
            obj.Total = PanCloudTypeRegistry.All.Count;
            obj.Tag = 1;
            return Json(obj);
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 保存「搜索设置」Tab 的配置项（按 Id 批量更新 conf_value）。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansearchconf:edit")]
        public async Task<ActionResult> SaveBatchJson([FromBody] List<PanConfEntity> entities)
        {
            TData obj = await panConfBLL.SaveBatch(entities);
            if (obj.Tag == 1)
            {
                obj.Message = "保存成功";
                // 前台搜索配置有 60 秒进程内缓存，保存后立即失效，避免修改后需要等待
                UserFrontendApiController.ClearSearchConfCache();
            }
            return Json(obj);
        }

        /// <summary>
        /// 保存独立 Tab 的配置（屏蔽词 / 搜索插件 / 网盘类型）：
        /// 只接受 ExtraKeys 中的 key，避免该接口被用来改写其它配置。
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pansearchconf:edit")]
        public async Task<ActionResult> SaveExtraJson([FromBody] SearchExtraParam param)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (param != null)
            {
                if (param.blockWords != null)
                {
                    values[BlockWordsKey] = NormalizeLines(param.blockWords);
                }
                if (param.plugins != null)
                {
                    values[PluginsKey] = NormalizeLines(param.plugins);
                }
                if (param.cloudTypes != null)
                {
                    values[CloudTypesKey] = NormalizeLines(param.cloudTypes);
                }
            }
            TData obj = await panConfBLL.SaveConfValues(values);
            if (obj.Tag == 1)
            {
                // 前台搜索配置有 60 秒进程内缓存，保存后立即失效，避免修改后需要等待
                UserFrontendApiController.ClearSearchConfCache();
            }
            return Json(obj);
        }

        /// <summary>独立 Tab 保存参数</summary>
        public class SearchExtraParam
        {
            /// <summary>屏蔽词（每行一个）</summary>
            public string blockWords { get; set; }
            /// <summary>搜索插件（每行一个）</summary>
            public string plugins { get; set; }
            /// <summary>网盘类型（逗号分隔）</summary>
            public string cloudTypes { get; set; }
        }

        /// <summary>
        /// 统一列表文本格式：换行/逗号/分号分隔的词，去空、去重后用英文逗号拼接。
        /// 插件名、网盘类型都不含逗号，统一存成逗号分隔便于前台解析。
        /// </summary>
        private static string NormalizeLines(string text)
        {
            IEnumerable<string> parts = (text ?? string.Empty)
                .Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", parts);
        }

        #endregion

        #region 私有方法

        /// <summary>是否搜索设置项：conf_type=1 或命中 SearchConfKeys</summary>
        private static bool IsSearchConf(PanConfEntity entity)
        {
            if (entity == null)
            {
                return false;
            }
            if ((entity.ConfType ?? 0) == 1)
            {
                return true;
            }
            return !string.IsNullOrEmpty(entity.ConfKey)
                && SearchConfKeys.Any(k => string.Equals(k, entity.ConfKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>是否为独立 Tab 维护的配置项（屏蔽词 / 搜索插件 / 网盘类型）</summary>
        private static bool IsExtraConf(PanConfEntity entity)
        {
            return entity != null && !string.IsNullOrEmpty(entity.ConfKey)
                && ExtraKeys.Any(k => string.Equals(k, entity.ConfKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>是否为已下线功能的遗留配置项（库里残留的旧记录，直接忽略）</summary>
        private static bool IsRetiredConf(PanConfEntity entity)
        {
            return entity != null && !string.IsNullOrEmpty(entity.ConfKey)
                && RetiredKeys.Any(k => string.Equals(k, entity.ConfKey, StringComparison.OrdinalIgnoreCase));
        }

        #endregion
    }
}
