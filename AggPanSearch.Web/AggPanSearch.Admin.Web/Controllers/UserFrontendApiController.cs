#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using AggPanSearch;
using AggPanSearch.Models;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Models;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Services;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Enum;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Admin.Web.Controllers
{
    /// <summary>
    /// 用户前台（二开版 guhua-xinyue-search）前端调用的降级/模拟接口。
    /// 二开版前端 JS 以绝对路径请求 /api/tool/*、/api/other/*、/api/search/*、/api/pancheck，
    /// 全网搜索（/api/other/web_search）已对接 AggPanSearch 进行真实网盘资源搜索；
    /// 链接检测（/api/pancheck）已对接 PanLinkCheckService 按平台真实探测；其余榜单等仍以模拟数据兜底。
    /// </summary>
    [ApiController]
    public class UserFrontendApiController : Controller
    {
        private readonly IUserFrontendService _service;
        private readonly AggPanSearchService _aggPanSearch;
        private readonly PanLinkCheckService _linkCheck;

        public UserFrontendApiController(IUserFrontendService service, AggPanSearchService aggPanSearch, PanLinkCheckService linkCheck)
        {
            _service = service;
            _aggPanSearch = aggPanSearch;
            _linkCheck = linkCheck;
        }

        private static object Success(dynamic data) => new { code = 200, message = "success", data = data };

        /// <summary>POST /api/tool/feedback</summary>
        [HttpPost("api/tool/feedback")]
        public IActionResult ToolFeedback([FromBody] Dictionary<string, object> body)
        {
            return Json(Success(new { message = "提交成功" }));
        }

        /// <summary>GET /api/other/local_search_check?title=xx&amp;is_type=x</summary>
        [HttpGet("api/other/local_search_check")]
        public IActionResult LocalSearchCheck(string title = "", string is_type = "")
        {
            return Json(Success(new { hasData = false, count = 0, items = new object[] { } }));
        }

        /// <summary>
        /// GET /api/other/get_search_stats —— 今日搜索量 / 总搜索量 / 平均搜索耗时
        /// 数据来源：pan_search_log 表（由 web_search 每次搜索写入），
        /// 读取走 PanSearchStatsService 的进程内缓存（今日 20s / 总量 5min / 平均耗时 60s），不会每次都 COUNT/AVG 全表。
        /// </summary>
        [HttpGet("api/other/get_search_stats")]
        public async Task<IActionResult> GetSearchStats()
        {
            int today = 0;
            int total = 0;
            int avgCostMs = 0;
            try
            {
                PanSearchLogBLL panSearchLogBLL = new PanSearchLogBLL();
                today = (await panSearchLogBLL.GetTodayCount().ConfigureAwait(false)).Data;
                total = (await panSearchLogBLL.GetTotalCount().ConfigureAwait(false)).Data;
                avgCostMs = (await panSearchLogBLL.GetAvgCost().ConfigureAwait(false)).Data;
            }
            catch
            {
                // 统计失败降级为 0，不影响前台展示
            }

            // 服务端已有缓存，这里再让浏览器缓存 10s，减少刷新/多标签页产生的重复请求
            Response.Headers["Cache-Control"] = "public, max-age=10";

            return Json(Success(new
            {
                today,
                today_formatted = PanSearchStatsService.FormatCount(today),
                total,
                total_formatted = PanSearchStatsService.FormatCount(total),
                // 平均搜索耗时：pan_search_log.cost_ms 平均值（含缓存命中），无数据时为空串
                avg_cost_ms = avgCostMs,
                avg_cost_formatted = PanSearchStatsService.FormatCost(avgCostMs)
            }));
        }

        /// <summary>POST /api/other/save_url (url, title) —— 链接解码/转存降级，直接返回入站链接。</summary>
        [HttpPost("api/other/save_url")]
        public IActionResult SaveUrl([FromBody] Dictionary<string, object> body)
        {
            var url = body != null && body.ContainsKey("url") ? body["url"]?.ToString() : "";
            // 前端可能已 encodeURIComponent
            string decoded = "";
            try { decoded = Uri.UnescapeDataString(url ?? ""); } catch { decoded = url ?? ""; }
            if (string.IsNullOrEmpty(decoded)) decoded = "https://pan.quark.cn/s/example";
            return Json(Success(new { url = decoded }));
        }

        /// <summary>
        /// GET /api/other/web_search —— 全网搜索：使用当前解决方案 AggPanSearch 项目搜索真实网盘资源。
        /// 返回一次性 JSON（结构与已部署的 AggPanSearch 接口一致）：
        /// { "code":200, "message":"success", "data":{ "total":N, "merged_by_type":{ "baidu":[ {url,password,note,datetime,source,images} ] } } }
        /// </summary>
        [HttpGet("api/other/web_search")]
        public async Task<IActionResult> WebSearch(string title = "", string is_type = "")
        {
            var kw = (title ?? "").Trim();
            if (kw.Length == 0)
                return Json(Success(new { total = 0, merged_by_type = new Dictionary<string, List<SearchWebItemDto>>() }));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            SearchResponse? response = null;
            var merged = new Dictionary<string, List<SearchWebItemDto>>(StringComparer.OrdinalIgnoreCase);
            bool success = false;

            // 后台「搜索设置」中的搜索范围配置：搜索插件 / 网盘类型 / 屏蔽词（60 秒进程内缓存）
            Dictionary<string, string> conf = await GetSearchConfAsync().ConfigureAwait(false);
            // 人工维护的屏蔽词 + 版权投诉审核通过后自动生成的关键词，统一转小写后同时用于
            // 「关键词预判」与「结果过滤」，避免逐条重复 ToLowerInvariant
            List<string> blockWords = SplitConfList(GetConfValue(conf, "search_block_words"))
                .Concat(SplitConfList(GetConfValue(conf, "copyright_block_keywords")))
                .Select(word => word.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // 屏蔽词预判：关键词本身命中屏蔽词时立刻返回空结果，
            // 不再发起全网搜索（多频道抓取需数秒，且结果必然被全部过滤）
            if (IsKeywordBlocked(kw, blockWords))
            {
                sw.Stop();
                try
                {
                    await new PanSearchLogBLL().AddLog(
                        kw, NetHelper.Ip, false, 0, string.Empty, (int)sw.ElapsedMilliseconds).ConfigureAwait(false);
                }
                catch
                {
                }
                return Json(Success(new
                {
                    total = 0,
                    merged_by_type = new Dictionary<string, List<SearchWebItemDto>>(),
                    blocked = true
                }));
            }

            // TG 频道改为读取 pan_search_channel 表（status=1），表为空时由 AggPanSearch 回退到内置默认频道列表
            List<string> channels = await GetSearchChannelsAsync().ConfigureAwait(false);
            List<string> plugins = SplitConfList(GetConfValue(conf, "search_plugins"));
            List<string> cloudTypes = SplitConfList(GetConfValue(conf, "search_cloud_types"));

            try
            {
                var request = new SearchRequest
                {
                    Keyword = kw,
                    // 按网盘类型合并后返回，结构与 AggPanSearch 的 merged_by_type 一致
                    ResultType = ResultTypes.Merge,
                    SourceType = SourceTypes.All,
                    // 未配置时传 null，由 AggPanSearch 使用内置频道 / 全部插件 / 全部网盘类型
                    Channels = channels.Count > 0 ? channels : null,
                    Plugins = plugins.Count > 0 ? plugins : null,
                    CloudTypes = cloudTypes.Count > 0 ? cloudTypes : null,
                };

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    response = await _aggPanSearch.SearchAsync(request, cts.Token).ConfigureAwait(false);
                }

                // 仅返回指定网盘类型（可选），否则返回全部已合并结果
                merged = BuildMergedByType(response, is_type);
                // 后台「搜索设置 - 屏蔽词过滤」：标题/描述命中的条目直接剔除
                merged = FilterBlocked(merged, blockWords);
                success = true;
            }
            catch
            {
                // 出错时仍返回正常结构，前端可展示"暂无搜索结果"
            }
            sw.Stop();

            // 记录搜索日志（命中缓存 / 结果总数 / 分类网盘结果数 / 耗时），写库失败不影响搜索本身
            try
            {
                await new PanSearchLogBLL().AddLog(
                    kw,
                    NetHelper.Ip,
                    response?.FromCache == true,
                    response?.Total ?? 0,
                    BuildTypeResultSummary(merged) ?? string.Empty,
                    (int)sw.ElapsedMilliseconds).ConfigureAwait(false);
            }
            catch
            {
            }

            if (!success)
            {
                return Json(Success(new { total = 0, merged_by_type = new Dictionary<string, List<SearchWebItemDto>>() }));
            }
            // total 以实际返回（已过滤屏蔽词、已按 is_type 筛选）的条数为准，避免前端显示的总数与列表不一致
            return Json(Success(new { total = merged.Values.Sum(v => v.Count), merged_by_type = merged }));
        }

        #region 版权投诉（前台提交）

        /// <summary>验证码 Session 键，与后台登录使用的 CaptchaCode 隔离</summary>
        private const string ComplaintCaptchaSessionKey = "ComplaintCaptchaCode";

        private static object Fail(string message) => new { code = 400, message };

        /// <summary>GET /api/other/copyright_captcha —— 版权投诉图形验证码（点击图片可刷新）</summary>
        [HttpGet("api/other/copyright_captcha")]
        [SupportedOSPlatform("windows")]
        public IActionResult CopyrightCaptcha()
        {
            Tuple<string, int> captchaCode = CaptchaHelper.GetCaptchaCode();
            byte[] bytes = CaptchaHelper.CreateCaptchaImage(captchaCode.Item1);
            // 与后台登录一致，存 int（SessionHelper 内部走 JSON 序列化，存 string 会带上引号）
            new SessionHelper().WriteSession(ComplaintCaptchaSessionKey, captchaCode.Item2);
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            return File(bytes, "image/jpeg");
        }

        /// <summary>
        /// POST /api/other/copyright_complaint —— 前台版权投诉提交（multipart/form-data）。
        /// 字段：title / infringing_url（选填）/ complainant / contact / description / captcha / 文件字段 evidence（1~3 张证明材料）。
        /// 校验顺序：字段与长度 → 验证码（一次性）→ 证明材料 → 同 IP 每小时限频。
        /// </summary>
        [HttpPost("api/other/copyright_complaint")]
        [RequestSizeLimit(20 * 1024 * 1024)]
        public async Task<IActionResult> SubmitCopyrightComplaint()
        {
            try
            {
                string title = (Request.Form["title"].ToString() ?? "").Trim();
                string infringingUrl = (Request.Form["infringing_url"].ToString() ?? "").Trim();
                string complainant = (Request.Form["complainant"].ToString() ?? "").Trim();
                string contact = (Request.Form["contact"].ToString() ?? "").Trim();
                string description = (Request.Form["description"].ToString() ?? "").Trim();
                string captcha = (Request.Form["captcha"].ToString() ?? "").Trim();

                // 验证码一次性：无论校验是否通过都从 Session 移除，避免重复提交
                SessionHelper session = new SessionHelper();
                string sessionCode = session.GetSession(ComplaintCaptchaSessionKey);
                session.RemoveSession(ComplaintCaptchaSessionKey);

                if (captcha.Length == 0)
                {
                    return Json(Fail("请填写验证码"));
                }
                if (string.IsNullOrEmpty(sessionCode))
                {
                    return Json(Fail("验证码已失效，请刷新验证码后重试"));
                }
                if (!string.Equals(captcha, sessionCode, StringComparison.OrdinalIgnoreCase))
                {
                    return Json(Fail("验证码错误，请重新输入"));
                }

                IFormFileCollection files = Request.Form.Files;
                if (files.Count > 3)
                {
                    return Json(Fail("证明材料最多上传 3 张"));
                }
                if (files.Count == 0)
                {
                    return Json(Fail("请上传证明材料（至少 1 张）"));
                }

                List<string> imagePaths = new List<string>();
                foreach (IFormFile file in files)
                {
                    if (file == null || file.Length == 0)
                    {
                        continue;
                    }
                    // FileHelper 一次只处理一个文件：按单元素集合逐个上传，校验扩展名与大小
                    TData<string> uploadObj = await FileHelper.UploadFile(
                        (int)UploadFileType.Complaint, new FormFileCollection { file });
                    if (uploadObj.Tag != 1)
                    {
                        return Json(Fail(uploadObj.Message));
                    }
                    imagePaths.Add(uploadObj.Data);
                }
                if (imagePaths.Count == 0)
                {
                    return Json(Fail("请上传证明材料（至少 1 张）"));
                }

                PanCopyrightComplaintEntity entity = new PanCopyrightComplaintEntity
                {
                    Title = title,
                    InfringingUrl = infringingUrl,
                    Complainant = complainant,
                    Contact = contact,
                    Description = description,
                    EvidenceImages = string.Join(",", imagePaths)
                };

                TData obj = await new PanCopyrightComplaintBLL().SubmitComplaint(entity, NetHelper.Ip);
                if (obj.Tag != 1)
                {
                    return Json(Fail(obj.Message));
                }
                return Json(new { code = 200, message = obj.Message, data = new { id = entity.Id } });
            }
            catch
            {
                // 不向前端暴露异常细节，也不记录联系方式等敏感信息
                return Json(Fail("提交失败，请稍后重试"));
            }
        }

        #endregion

        #region 搜索设置（屏蔽词 / TG频道 / 搜索插件 / 网盘类型）

        private static Dictionary<string, string> _searchConfCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _searchConfExpire = DateTime.MinValue;
        private static readonly object _searchConfLock = new object();

        /// <summary>TG 频道列表缓存（来自 pan_search_channel，status=1）</summary>
        private static List<string> _searchChannelCache = new List<string>();
        private static DateTime _searchChannelExpire = DateTime.MinValue;
        private static readonly object _searchChannelLock = new object();

        /// <summary>
        /// 让前台搜索的 TG 频道缓存立即失效（后台「TG频道」页新增/修改/删除/启停后调用）。
        /// </summary>
        public static void ClearSearchChannelCache()
        {
            lock (_searchChannelLock)
            {
                _searchChannelExpire = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 读取参与搜索的 TG 频道（pan_search_channel 中 status=1 的记录，按 sort 升序）。
        /// 60 秒进程内缓存；表为空或读取失败时返回空列表，由 AggPanSearch 回退到内置默认频道列表。
        /// </summary>
        private static async Task<List<string>> GetSearchChannelsAsync()
        {
            lock (_searchChannelLock)
            {
                if (DateTime.Now < _searchChannelExpire)
                {
                    return _searchChannelCache;
                }
            }

            List<string> channels = new List<string>();
            try
            {
                TData<List<string>> obj = await new PanSearchChannelBLL().GetEnabledNames().ConfigureAwait(false);
                if (obj.Tag == 1 && obj.Data != null)
                {
                    channels = obj.Data;
                }
            }
            catch
            {
                // 表不存在 / 读取失败：按未配置处理，回退内置频道
            }

            lock (_searchChannelLock)
            {
                _searchChannelCache = channels;
                _searchChannelExpire = DateTime.Now.AddSeconds(60);
            }
            return channels;
        }

        /// <summary>
        /// 读取后台「搜索设置」中影响搜索的配置项（pan_conf）：
        /// search_block_words / search_plugins / search_cloud_types。
        /// 搜索接口调用频繁，做 60 秒进程内缓存，避免每次搜索都查库；后台保存后最多 1 分钟生效。
        /// </summary>
        private static async Task<Dictionary<string, string>> GetSearchConfAsync()
        {
            lock (_searchConfLock)
            {
                if (DateTime.Now < _searchConfExpire)
                {
                    return _searchConfCache;
                }
            }

            Dictionary<string, string> conf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var keys = new List<string>
                {
                    "search_block_words", "search_plugins", "search_cloud_types",
                    PanCopyrightComplaintBLL.KeywordsConfKey
                };
                TData<Dictionary<string, string>> obj =
                    await new PanConfBLL().GetConfValues(keys).ConfigureAwait(false);
                if (obj.Tag == 1 && obj.Data != null)
                {
                    conf = new Dictionary<string, string>(obj.Data, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 配置读取失败不影响搜索，按未配置处理
            }

            lock (_searchConfLock)
            {
                _searchConfCache = conf;
                _searchConfExpire = DateTime.Now.AddSeconds(60);
            }
            return conf;
        }

        /// <summary>
        /// 使搜索配置缓存立即失效：后台保存「搜索设置」后调用，避免最多 60 秒才生效。
        /// </summary>
        public static void ClearSearchConfCache()
        {
            lock (_searchConfLock)
            {
                _searchConfCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _searchConfExpire = DateTime.MinValue;
            }
        }

        private static string GetConfValue(Dictionary<string, string> conf, string key)
        {
            if (conf != null && conf.TryGetValue(key, out string? value) && value != null)
            {
                return value;
            }
            return string.Empty;
        }

        /// <summary>拆分后台配置的列表文本（兼容换行 / 逗号 / 分号分隔，去空去重）。</summary>
        private static List<string> SplitConfList(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '\r', '\n', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>剔除标题/描述命中屏蔽词的结果；某网盘类型被全部剔除时直接去掉该分组</summary>
        private static Dictionary<string, List<SearchWebItemDto>> FilterBlocked(Dictionary<string, List<SearchWebItemDto>> merged, List<string> blockWords)
        {
            if (merged == null || merged.Count == 0 || blockWords == null || blockWords.Count == 0)
            {
                return merged!;
            }

            var result = new Dictionary<string, List<SearchWebItemDto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, items) in merged)
            {
                if (items == null || items.Count == 0)
                {
                    continue;
                }
                var kept = items.Where(i => !IsBlockedItem(i, blockWords)).ToList();
                if (kept.Count > 0)
                {
                    result[type] = kept;
                }
            }
            return result;
        }

        /// <summary>
        /// 关键词是否命中屏蔽词：关键词（转小写）包含任一屏蔽词即视为命中，大小写不敏感。
        /// 命中时调用方直接返回空结果，跳过全网搜索，把数秒级抓取缩短到毫秒级。
        /// 说明：blockWords 需为调用方预先转小写的列表。
        /// </summary>
        private static bool IsKeywordBlocked(string keyword, List<string> blockWords)
        {
            if (string.IsNullOrWhiteSpace(keyword) || blockWords == null || blockWords.Count == 0)
            {
                return false;
            }
            string text = keyword.ToLowerInvariant();
            foreach (string word in blockWords)
            {
                if (word.Length > 0 && text.Contains(word, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>剔除标题/描述命中屏蔽词的结果（blockWords 需为已转小写的列表）</summary>
        private static bool IsBlockedItem(SearchWebItemDto item, List<string> blockWords)
        {
            if (item == null)
            {
                return false;
            }
            string text = ((item.Note ?? "") + " " + (item.Source ?? "")).ToLowerInvariant();
            if (text.Trim().Length == 0)
            {
                return false;
            }
            foreach (string word in blockWords)
            {
                if (word.Length > 0 && text.Contains(word, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        /// <summary>
        /// 汇总各网盘类型的结果条数，形如：夸克网盘:12;百度网盘:3（无结果返回 null）。
        /// </summary>
        private static string? BuildTypeResultSummary(Dictionary<string, List<SearchWebItemDto>> merged)
        {
            if (merged == null || merged.Count == 0) return null;
            var parts = merged
                .Where(kv => kv.Value != null && kv.Value.Count > 0)
                .Select(kv => $"{AggPanSearchName(IsTypeFromAggPanSearch(kv.Key))}:{kv.Value.Count}")
                .ToList();
            return parts.Count == 0 ? null : string.Join(";", parts);
        }

        /// <summary>
        /// 将 AggPanSearch 按网盘类型合并的结果转换为前端可读记录（按网盘类型分组）。
        /// is_type（0-10 数值）非空时仅保留该网盘类型。
        /// </summary>
        private static Dictionary<string, List<SearchWebItemDto>> BuildMergedByType(SearchResponse? response, string isType)
        {
            var result = new Dictionary<string, List<SearchWebItemDto>>(StringComparer.OrdinalIgnoreCase);
            if (response?.MergedByType is null) return result;

            var filterType = AggPanSearchTypeFromIsType(isType);
            foreach (var (type, links) in response.MergedByType)
            {
                // 若指定了网盘类型且当前组不符，则跳过
                if (!string.IsNullOrEmpty(filterType) &&
                    !string.Equals(type, filterType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (links is null) continue;
                var items = links.Where(l => !string.IsNullOrEmpty(l.Url)).Select(l => new SearchWebItemDto
                {
                    Url = l.Url,
                    Password = l.Password ?? "",
                    Code = l.Password ?? "",
                    Note = l.Note,
                    Datetime = l.Datetime != null ? l.Datetime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    Source = l.Source ?? "",
                    Images = l.Images,
                    Pantype = AggPanSearchName(IsTypeFromAggPanSearch(type)),
                    IsType = IsTypeFromAggPanSearch(type),
                }).ToList();
                result[type] = items;
            }
            return result;
        }

        /// <summary>前端友好的单条网盘搜索结果（对应 AggPanSearch 的 MergedLink 并附带网盘类型信息）。</summary>
        private sealed class SearchWebItemDto
        {
            [JsonProperty("url")]
            public string Url { get; set; } = "";
            [JsonProperty("password")]
            public string Password { get; set; } = "";
            [JsonProperty("code")]
            public string Code { get; set; } = "";
            [JsonProperty("note")]
            public string Note { get; set; } = "";
            [JsonProperty("datetime")]
            public string Datetime { get; set; } = "";
            [JsonProperty("source")]
            public string Source { get; set; } = "";
            [JsonProperty("images")]
            public List<string>? Images { get; set; }
            [JsonProperty("pantype")]
            public string Pantype { get; set; } = "";
            [JsonProperty("is_type")]
            public int IsType { get; set; } = -1;
        }

        /// <summary>前端 is_type -> AggPanSearch 网盘类型字符串；无法映射返回 null（统一由 PanCloudTypeRegistry 提供）。</summary>
        private static string? AggPanSearchTypeFromIsType(string isType)
        {
            int value;
            if (!int.TryParse(isType, out value))
            {
                return null;
            }
            return PanCloudTypeRegistry.ToValue(value);
        }

        /// <summary>AggPanSearch 网盘类型字符串 -> 前端数值 is_type；无法映射返回 -1。</summary>
        private static int IsTypeFromAggPanSearch(string? type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return -1;
            }
            return PanCloudTypeRegistry.ToIsType(type);
        }

        /// <summary>前端网盘类型名称（与 yfs.runtime.js 的 panTypeMap 保持一致，取自 PanCloudTypeRegistry）。</summary>
        private static string AggPanSearchName(int isType)
        {
            string name = PanCloudTypeRegistry.ToText(isType);
            return string.IsNullOrEmpty(name) ? "网盘资源" : name;
        }

        /// <summary>POST /api/search/incrementViews</summary>
        [HttpPost("api/search/incrementViews")]
        public IActionResult IncrementViews([FromBody] Dictionary<string, object> body)
        {
            return Json(Success(new { }));
        }

        /// <summary>
        /// POST /api/pancheck —— 批量网盘链接有效性检测（真实探测，按网盘类型调用对应官方接口）。
        /// 入参：{ links: ["https://...", {"url": "...", "password": "..."}] }
        /// 出参：{ success: true, results: [{ url, valid, message, type }] }，valid 三态：true 有效 / false 失效 / null 不确定或暂不支持。
        /// </summary>
        [HttpPost("api/pancheck")]
        public async Task<IActionResult> PanCheck([FromBody] Dictionary<string, object> body, CancellationToken ct)
        {
            var links = new List<PanCheckLink>();
            if (body != null && body.TryGetValue("links", out var raw) && raw is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var token in arr)
                {
                    if (token is Newtonsoft.Json.Linq.JValue jv &&
                        jv.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    {
                        links.Add(new PanCheckLink { Url = jv.Value?.ToString() ?? "" });
                    }
                    else if (token is Newtonsoft.Json.Linq.JObject obj)
                    {
                        links.Add(new PanCheckLink
                        {
                            Url = obj["url"]?.ToString() ?? "",
                            Password = obj["password"]?.ToString() ?? "",
                        });
                    }
                }
            }

            var results = await _linkCheck.CheckAsync(links, ct);
            return Json(new
            {
                success = true,
                results = results.Select(r => new
                {
                    url = r.Url,
                    valid = r.Valid,
                    message = r.Message,
                    type = r.Type,
                }).ToList(),
            });
        }

        }
}