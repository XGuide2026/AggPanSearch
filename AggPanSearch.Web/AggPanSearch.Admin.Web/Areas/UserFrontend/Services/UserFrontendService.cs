using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Models;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Services
{
    /// <summary>
    /// 用户前台数据服务（二开版 guhua-xinyue-search 对接）。
    /// 提供示例/模拟数据以支撑页面渲染；后续可替换为真实搜索/榜单接口。
    /// </summary>
    public interface IUserFrontendService
    {
        SiteConfig GetConfig();
        IndexPageModel GetIndexData();
        SearchPageModel Search(string keyword, int pageNo, int pageSize, int categoryId);
        DetailPageModel GetDetail(string id);
        CategoryPageModel GetCategory(int pageNo, int pageSize, long categoryId = 0);
        List<CategoryItem> GetCategories();
    }

    public class UserFrontendService : IUserFrontendService
    {
        private readonly List<ResourceItem> _allItems;
        private static readonly string[] PanNames = { "夸克网盘", "阿里云盘", "百度网盘", "UC网盘", "迅雷云盘", "123云盘", "115网盘", "天翼云盘", "移动云盘", "磁力链接", "光鸭网盘" };
        // 搜索统计（今日/总量）走进程内缓存，首页多次读取配置也不会重复 COUNT
        private static readonly PanSearchStatsService StatsService = new PanSearchStatsService();

        public UserFrontendService()
        {
            _allItems = BuildSampleItems();
        }

        /// <summary>
        /// 站点配置：优先从 pan_conf 表（后台"基础设置"Tab）读取，按 conf_key 映射到 SiteConfig；
        /// 表不存在/读取失败时回退内置默认值，保证前台始终可渲染。
        /// </summary>
        public SiteConfig GetConfig()
        {
            var config = BuildDefaultConfig();
            try
            {
                var obj = new PanConfBLL().GetList(null).GetAwaiter().GetResult();
                if (obj != null && obj.Tag == 1 && obj.Data != null)
                {
                    ApplyConfig(obj.Data, config);
                }
            }
            catch
            {
                // pan_conf 表未创建或数据库不可用：回退内置默认配置
            }

            // 首页"今日搜索 / 总搜索量 / 搜索耗时"首屏直出（服务端渲染），避免先显示占位数字再被前端覆盖造成闪烁。
            // GetStats 读的是进程内快照，通常零数据库开销；跨天/首次最多等待 300ms，失败降级为 0。
            try
            {
                PanSearchStatsSnapshot stats = StatsService.GetStats();
                config.TodayFormatted = PanSearchStatsService.FormatCount(stats.Today);
                config.TotalFormatted = PanSearchStatsService.FormatCount(stats.Total);
                // 搜索耗时 = pan_search_log.cost_ms 的平均值（含缓存命中）；无数据时保留配置/默认值
                string avgCost = PanSearchStatsService.FormatCost(stats.AvgCostMs);
                if (!string.IsNullOrEmpty(avgCost))
                {
                    config.LoadTime = avgCost;
                }
            }
            catch
            {
                // 统计不可用不影响页面渲染
            }
            return config;
        }

        /// <summary>
        /// 将 pan_conf 表配置按 conf_key → SiteConfig 属性（下划线转帕斯卡）映射。
        /// </summary>
        private static void ApplyConfig(List<PanConfEntity> list, SiteConfig config)
        {
            var props = typeof(SiteConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            // conf_spec=5「选择二维码」的配置项（如 ad_popup_image）保存的是「二维码设置」Tab 里二维码的 conf_key，
            // 不是图片地址；这里先把整张 key → 图片路径 的表建好，循环里再做一次 O(1) 解析。
            var qrcodeMap = BuildQrcodeMap(list);
            foreach (var item in list)
            {
                if (string.IsNullOrEmpty(item.ConfKey) || item.ConfValue == null)
                {
                    continue;
                }
                var prop = props.FirstOrDefault(p => string.Equals(p.Name, ToPascalCase(item.ConfKey), StringComparison.OrdinalIgnoreCase));
                if (prop == null || !prop.CanWrite)
                {
                    continue;
                }
                string confValue = item.ConfValue;
                try
                {
                    if (prop.PropertyType == typeof(int))
                    {
                        if (int.TryParse(confValue, out var n))
                        {
                            prop.SetValue(config, n);
                        }
                    }
                    else if (prop.PropertyType == typeof(List<string[]>))
                    {
                        var arr = JsonConvert.DeserializeObject<List<string[]>>(confValue);
                        if (arr != null)
                        {
                            prop.SetValue(config, arr);
                        }
                    }
                    else if (prop.PropertyType != typeof(string))
                    {
                        prop.SetValue(config, JsonConvert.DeserializeObject(confValue, prop.PropertyType));
                    }
                    else
                    {
                        // 「选择二维码」存的是二维码 key，前台要用图片地址，所以统一解析一次（未命中原样返回）
                        prop.SetValue(config, ResolveQrcodeValue(item, confValue, qrcodeMap));
                    }
                }
                catch
                {
                    // 单条配置解析失败不影响其它配置项
                }
            }
            // 首页右侧「联系方式」悬浮方块（conf_type=13，两组独立开关）
            ApplyContactFloat(list, config, qrcodeMap);
            // 后台「搜索设置 - 网盘类型」禁用的类型不在前台展示标签（首页 / 搜索页 / 前端 panTypeMap 同源）
            FilterEnabledCloudTypes(list, config);
            // pan_type_map 配置后派生两个 JSON 字段，保证前端 panTypeJson/panTypeKeysJson 一致
            if (config.PanTypeMap != null && config.PanTypeMap.Count > 0)
            {
                var map = config.PanTypeMap.ToDictionary(kv => kv[0], kv => kv[1]);
                config.PanTypeJson = JsonConvert.SerializeObject(map, Formatting.None);
                config.PanTypeKeysJson = JsonConvert.SerializeObject(map.Keys.ToList(), Formatting.None);
            }
        }

        /// <summary>
        /// 是否「二维码设置」Tab（conf_type=10）里的配置项。
        /// 归类规则与后台 PanConfIndex.cshtml 的 isQrConfItem 对齐：conf_type 有值时必须是 10，缺失时才按 qr_ 前缀兜底。
        /// </summary>
        private static bool IsQrConfItem(PanConfEntity item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ConfKey))
            {
                return false;
            }
            if (item.ConfType.HasValue && item.ConfType.Value > 0)
            {
                return item.ConfType.Value == 10;
            }
            return item.ConfKey.Trim().StartsWith("qr_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 构建「二维码 key → 图片路径」映射（只收已上传图片的二维码项，值为空的不参与）。
        /// 键统一忽略大小写，避免后台 key 大小写不一致导致解析不到。
        /// </summary>
        private static Dictionary<string, string> BuildQrcodeMap(List<PanConfEntity> list)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (list == null)
            {
                return map;
            }
            foreach (var item in list)
            {
                if (!IsQrConfItem(item))
                {
                    continue;
                }
                string path = (item.ConfValue ?? "").Trim();
                if (path.Length == 0)
                {
                    continue;
                }
                map[item.ConfKey.Trim()] = path;
            }
            return map;
        }

        /// <summary>把「选择二维码」控件（conf_spec=5）存的二维码 key 解析成图片路径；已是路径或未命中映射时原样返回（最多 3 跳，防自引用）。</summary>
        private static string ResolveQrcodeValue(PanConfEntity item, string value, Dictionary<string, string> map)
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0 || map == null || map.Count == 0)
            {
                return value;
            }
            // 只处理「选择二维码」控件；同时兜底处理 conf_spec 缺失但值本身就是二维码 key 的历史数据
            bool isQrcodeField = item != null && item.ConfSpec == 5;
            if (!isQrcodeField && !map.ContainsKey(v))
            {
                return value;
            }
            if (IsImagePath(v))
            {
                return value;
            }
            string current = v;
            for (int hop = 0; hop < 3; hop++)
            {
                if (!map.TryGetValue(current, out string next))
                {
                    break;
                }
                next = (next ?? "").Trim();
                if (next.Length == 0 || string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = next;
                if (IsImagePath(current))
                {
                    break;
                }
            }
            return current;
        }

        /// <summary>值是否已经是可直接使用的图片地址（绝对路径、外链、data URI 或带目录的相对路径）。</summary>
        private static bool IsImagePath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            return value.Contains('/')
                || value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>「联系链接」配置键：contact_linkN_enable / contact_linkN_url / contact_linkN_icon（N 从 1 起，可任意多个）。</summary>
        private static readonly Regex ContactLinkKeyRegex = new Regex(@"^contact_link(\d+)_(enable|url|icon)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 首页右侧「联系方式」悬浮方块（conf_type=13）：contact_qrcode1/2 两组各自的开关、图片、标题、简介；
        /// 以及底部的「联系链接」contact_linkN_enable/url/icon（数量不限，按 N 升序输出）。
        /// </summary>
        private static void ApplyContactFloat(List<PanConfEntity> list, SiteConfig config, Dictionary<string, string> qrcodeMap)
        {
            if (config.ContactLinks == null)
            {
                config.ContactLinks = new List<ContactLinkItem>();
            }
            if (list == null || list.Count == 0)
            {
                return;
            }
            // N -> (开关, 地址, 图标)：同一序号的三个 key 分散在不同行，先归并再统一过滤
            var links = new Dictionary<int, (int Enable, string Url, string Icon)>();
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ConfKey))
                {
                    continue;
                }
                string key = item.ConfKey.Trim().ToLowerInvariant();
                string value = (item.ConfValue ?? "").Trim();

                // 联系链接：不限制 conf_type（后台新增配置项时可能留空或未归类为 13），key 前缀已足够唯一
                Match match = ContactLinkKeyRegex.Match(key);
                if (match.Success)
                {
                    if (!int.TryParse(match.Groups[1].Value, out int index) || index <= 0)
                    {
                        continue;
                    }
                    links.TryGetValue(index, out var current);
                    switch (match.Groups[2].Value)
                    {
                        case "enable":
                            current.Enable = ToSwitchValue(value);
                            break;
                        case "url":
                            current.Url = value;
                            break;
                        case "icon":
                            // 图标为多行文本，直接粘贴 SVG 源码（也兼容图片地址，视图层区分渲染）
                            current.Icon = value;
                            break;
                    }
                    links[index] = current;
                    continue;
                }

                if (item.ConfType != 13)
                {
                    continue;
                }
                switch (key)
                {
                    case "contact_qrcode1_enable":
                        config.ContactQrcode1Enable = ToSwitchValue(value);
                        break;
                    case "contact_qrcode1":
                        config.ContactQrcode1Image = ResolveQrcodeValue(item, value, qrcodeMap);
                        break;
                    case "contact_qrcode1_title":
                        config.ContactQrcode1Title = value;
                        break;
                    case "contact_qrcode1_desc":
                        config.ContactQrcode1Desc = value;
                        break;
                    case "contact_qrcode2_enable":
                        config.ContactQrcode2Enable = ToSwitchValue(value);
                        break;
                    case "contact_qrcode2":
                        config.ContactQrcode2Image = ResolveQrcodeValue(item, value, qrcodeMap);
                        break;
                    case "contact_qrcode2_title":
                        config.ContactQrcode2Title = value;
                        break;
                    case "contact_qrcode2_desc":
                        config.ContactQrcode2Desc = value;
                        break;
                }
            }
            config.ContactLinks = BuildContactLinks(links);
        }

        /// <summary>
        /// 把归并后的 contact_linkN_* 转成前台可渲染的列表：跳过未开启、协议非法、图标为空的项；
        /// 图标支持「多行文本粘贴的 SVG 源码」（截取到 &lt;svg 起始，避免前面混入说明文字），也兼容图片地址。
        /// </summary>
        private static List<ContactLinkItem> BuildContactLinks(Dictionary<int, (int Enable, string Url, string Icon)> links)
        {
            var result = new List<ContactLinkItem>();
            foreach (var kv in links.OrderBy(k => k.Key))
            {
                // 未开启的项不展示（enable 缺失视为关闭，与二维码组开关一致）
                if (kv.Value.Enable != 1)
                {
                    continue;
                }
                // 地址缺协议头（如后台直接填 github.com）时补 https://，避免整条链接被过滤
                string url = NormalizeLinkUrl(kv.Value.Url);
                if (url.Length == 0)
                {
                    continue;
                }
                string icon = (kv.Value.Icon ?? "").Trim().TrimStart('\uFEFF').Trim();
                if (icon.Length == 0)
                {
                    continue;
                }
                bool isSvg = IsInlineSvg(icon);
                if (isSvg)
                {
                    // 只保留 <svg ...> 之后的内容，去掉可能粘贴进来的前后说明文字
                    icon = icon.Substring(icon.IndexOf("<svg", StringComparison.OrdinalIgnoreCase));
                    if (icon.IndexOf("</svg>", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }
                result.Add(new ContactLinkItem { Url = url, Icon = icon, IsSvg = isSvg });
            }
            return result;
        }

        /// <summary>
        /// 图标值是否为内联 SVG 源码：含 &lt;svg 与 &lt;/svg&gt;，且不含脚本/事件属性（后台粘贴的图标代码安全兜底）。
        /// </summary>
        private static bool IsInlineSvg(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            if (value.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
            if (value.IndexOf("</svg>", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
            return value.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("onload=", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// 规范化联系链接地址：站内相对路径、带协议（http/https/mailto/tel/tg 等）的原样返回；
        /// 只填了域名（github.com、www.a.com/x）时补 https://；其余（含空格、javascript:、data:）视为非法返回空串。
        /// </summary>
        private static string NormalizeLinkUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "";
            }
            string v = url.Trim();
            // 协议相对 //example.com
            if (v.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + v;
            }
            // 站内相对路径
            if (v.StartsWith("/", StringComparison.Ordinal))
            {
                return v;
            }
            int schemeIdx = v.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx > 0)
            {
                string scheme = v.Substring(0, schemeIdx).Trim().ToLowerInvariant();
                return IsBlockedScheme(scheme) ? "" : v;
            }
            int colonIdx = v.IndexOf(':');
            if (colonIdx > 0 && v.IndexOf(' ') < 0)
            {
                // mailto: / tel: / weixin: 等非层级协议（scheme 不含点，避免把 github.com:8080 当成协议）
                string scheme = v.Substring(0, colonIdx).Trim().ToLowerInvariant();
                if (scheme.Length > 0 && scheme.IndexOf('.') < 0 && IsSchemeName(scheme))
                {
                    return IsBlockedScheme(scheme) ? "" : v;
                }
            }
            // 无协议：含空格或没有点的视为非法；形如 github.com / www.a.com/x 的补 https://
            if (v.IndexOf(' ') >= 0 || v.IndexOf('.') < 0)
            {
                return "";
            }
            return "https://" + v;
        }

        /// <summary>协议名是否合法（字母开头，仅字母/数字/+/-）。</summary>
        private static bool IsSchemeName(string scheme)
        {
            if (scheme.Length == 0 || !char.IsLetter(scheme[0]))
            {
                return false;
            }
            foreach (char c in scheme)
            {
                if (!char.IsLetterOrDigit(c) && c != '+' && c != '-')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>可执行脚本类协议，直接拒绝。</summary>
        private static bool IsBlockedScheme(string scheme)
        {
            return scheme == "javascript" || scheme == "data" || scheme == "vbscript" || scheme == "file";
        }

        /// <summary>「是否开启」类配置值转 0/1：兼容 1/0、true/false、on/off、「开启/关闭」、「是/否」。</summary>
        private static int ToSwitchValue(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return (v == "1" || v == "true" || v == "on" || v == "yes" || v == "开启" || v == "是" || v == "启用") ? 1 : 0;
        }

        /// <summary>
        /// 按后台「搜索设置 - 网盘类型」启用状态过滤前台展示的网盘标签。
        /// 只隐藏"注册表内但未启用"的类型；pan_conf 没有 search_cloud_types 或值为空表示全部启用，保持原样；
        /// 配置里没有可识别的类型名、或过滤后为空时也保持原样，避免前台标签区变成空白。
        /// </summary>
        private static void FilterEnabledCloudTypes(List<PanConfEntity> list, SiteConfig config)
        {
            if (config.PanTypeMap == null || config.PanTypeMap.Count == 0 || list == null)
            {
                return;
            }
            PanConfEntity conf = list.FirstOrDefault(e => string.Equals(e.ConfKey, "search_cloud_types", StringComparison.OrdinalIgnoreCase));
            if (conf == null || string.IsNullOrWhiteSpace(conf.ConfValue))
            {
                return;
            }
            var enabled = new HashSet<int>();
            foreach (string part in conf.ConfValue.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                PanCloudType type = PanCloudTypeRegistry.Find(part);
                if (type != null)
                {
                    enabled.Add(type.IsType);
                }
            }
            if (enabled.Count == 0)
            {
                return;
            }
            List<string[]> filtered = config.PanTypeMap
                .Where(kv =>
                {
                    if (kv == null || kv.Length < 2 || !int.TryParse(kv[0], out int key))
                    {
                        return true;
                    }
                    // 注册表外的自定义类型保持原样；已注册的只保留启用项
                    return PanCloudTypeRegistry.All.All(t => t.IsType != key) || enabled.Contains(key);
                })
                .ToList();
            if (filtered.Count > 0)
            {
                config.PanTypeMap = filtered;
            }
        }

        /// <summary>conf_key（下划线）转 SiteConfig 属性名（帕斯卡），如 home_bg → HomeBg。</summary>
        private static string ToPascalCase(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }
            var parts = key.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1)
                {
                    sb.Append(p.Substring(1));
                }
            }
            return sb.ToString();
        }

        private SiteConfig BuildDefaultConfig()
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < PanNames.Length; i++) map[i.ToString()] = PanNames[i];

            return new SiteConfig
            {
                HomeBg = "https://static.xinyuepan.com/home_bg.png",
                AppName = "妙搜云",
                AppNameHide = 0,
                AppSubname = "专业网盘资源搜索分享平台",
                HomeColor = "#3e3e3e",
                HomeTheme = "#ff6b6b",
                // 主题由后台"网盘管理-切换主题"统一设置，默认暖红且不开放访客切换
                Theme = "red",
                ThemeSwitch = 0,
                HomeBackground = "#fafafa",
                OtherBackground = "#ffffff",
                FooterDec = "本站仅为资源索引，不存储、不传播任何文件，仅供学习交流使用，请勿用于商业用途。",
                FooterCopyright = "© 2026 妙搜云 · ",
                SearchTips = "未找到，可换个关键词尝试哦~",
                PcType = 0,
                IsQuan = 1,
                HomeNew = 0,
                PanTypeMap = map.Select(kv => new[] { kv.Key, kv.Value }).ToList(),
                PanTypeJson = JsonConvert.SerializeObject(map, Formatting.None),
                PanTypeKeysJson = JsonConvert.SerializeObject(map.Keys.Select(k => k.ToString()).ToList(), Formatting.None),
                TodayFormatted = "0",
                TotalFormatted = "0",
                // 兜底值：有搜索日志时会被 pan_search_log.cost_ms 的平均耗时覆盖
                LoadTime = "0.26s"
            };
        }

        public IndexPageModel GetIndexData()
        {
            // 直接复用"资源分类"页的取数逻辑：分类列表 + 最新资源来自同一份查询结果，
            // 首页不再单独查数据库，保证两处数据一致（都是 pan_source_category / pan_source）
            var category = GetCategory(1, 6, 0);
            return new IndexPageModel
            {
                NewList = _allItems.Take(8).ToList(),
                Categories = category.Categories,
                CategoryList = category.Items,
                HotList = BuildHotList(),
                RankList = new List<string> { "电影", "电视剧", "动漫", "综艺", "短剧" }
            };
        }

        public SearchPageModel Search(string keyword, int pageNo, int pageSize, int categoryId)
        {
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "";

            var items = _allItems
                .Where(i => string.IsNullOrEmpty(keyword) || i.Title.Contains(keyword))
                .ToList();

            int total = items.Count;
            var paged = items.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();

            return new SearchPageModel
            {
                Keyword = keyword,
                PageNo = pageNo,
                PageSize = pageSize,
                TotalResult = total,
                CategoryId = categoryId,
                CategoryName = categoryId > 0 ? "分类" + categoryId : "",
                Items = paged,
                DisplayList = _allItems.Take(10).ToList(),
                HotList = BuildHotList(),
                RankList = new List<string> { "热搜", "最新" },
                FirstKey = "0",
                blocked = 0,
                Category = BuildCategoryMap()
            };
        }

        /// <summary>
        /// 资源详情页：按 URL 中的 Id 读取 pan_source 真实记录（过滤软删除/禁用），
        /// 读取失败或记录不存在时回退示例数据，保证页面始终可渲染。
        /// </summary>
        public DetailPageModel GetDetail(string id)
        {
            var categoryMap = BuildSourceCategoryMap();
            long sourceId = 0;
            PanSourceEntity source = null;

            if (long.TryParse((id ?? "").Trim(), out sourceId))
            {
                try
                {
                    var obj = new PanSourceBLL().GetEntity(sourceId).GetAwaiter().GetResult();
                    if (obj != null && obj.Tag == 1 && obj.Data != null)
                    {
                        bool hidden = (obj.Data.IsDelete.HasValue && obj.Data.IsDelete.Value == 1)
                                   || (obj.Data.Status.HasValue && obj.Data.Status.Value == 0);
                        if (!hidden)
                        {
                            source = obj.Data;
                        }
                    }
                }
                catch
                {
                    // pan_source 表不存在或数据库不可用时按示例数据处理
                }
            }

            // 真实记录优先；库中查不到时按示例数据匹配，仍匹配不到则取第一条示例
            var item = source != null
                ? ToResourceItem(source, categoryMap)
                : (_allItems.FirstOrDefault(i => i.Id == id) ?? _allItems.First());

            // 轮播图：vod_pic 多图（后台编辑页最多 5 张，分隔符 ;），首张即列表页封面
            var pics = ImageUrlList(item.ImageUrl, source == null ? item.Src : "");

            var detail = new ResourceDetail
            {
                Id = item.Id,
                Title = item.Title,
                Url = item.Url,
                Code = item.Code,
                Times = item.Times,
                Src = item.Src,
                Description = item.Description,
                PageViews = source != null ? (source.PageViews ?? 0) : 0,
                IsType = item.IsType,
                ImageUrl = item.ImageUrl,
                SourceCategoryId = item.SourceCategoryId,
                CategoryName = item.CategoryName,
                VodPic = pics.Count > 0 ? pics[0] : "",
                VodPics = pics,
                VodContent = string.IsNullOrWhiteSpace(item.VodContent)
                    ? "本资源为聚合索引结果，点击下方按钮可直接访问网盘或复制链接保存。"
                    : item.VodContent
            };

            // 相关资源：优先取同分类最新资源，不足时用示例数据补齐
            var same = new List<ResourceItem>();
            if (source != null)
            {
                try
                {
                    var category = new PanSourceBLL().GetCategoryList(1, 8, source.SourceCategoryId ?? 0).GetAwaiter().GetResult();
                    if (category != null && category.Tag == 1 && category.Data != null)
                    {
                        same = category.Data
                            .Where(e => e.Id != sourceId)
                            .Take(6)
                            .Select(e => ToResourceItem(e, categoryMap))
                            .ToList();
                    }
                }
                catch
                {
                    // 取相关资源失败时留空，由下方示例数据补齐
                }
            }
            if (same.Count == 0)
            {
                same = _allItems.Where(i => i.Id != item.Id).Take(6).ToList();
            }

            return new DetailPageModel
            {
                Detail = detail,
                SameList = same,
                HotList = BuildHotList(),
                RankList = new List<string> { "热搜", "最新" },
                PcType = GetConfig().PcType
            };
        }

        /// <summary>
        /// 资源分类列表（pan_source_category）：按 sort 升序，过滤后台禁用分类。
        /// "资源分类"页与首页共用同一份逻辑，避免两处重复查询与映射。
        /// </summary>
        public List<CategoryItem> GetCategories()
        {
            var list = new List<CategoryItem>();
            try
            {
                var categoryObj = new PanSourceCategoryBLL().GetList(null).GetAwaiter().GetResult();
                if (categoryObj != null && categoryObj.Tag == 1 && categoryObj.Data != null)
                {
                    list = categoryObj.Data
                        .Where(c => c.Id.HasValue && !string.IsNullOrWhiteSpace(c.Name))
                        .Where(c => c.Status == null || c.Status != 0)
                        .OrderBy(c => c.Sort ?? 0)
                        .ThenBy(c => c.Id)
                        .Select(c => new CategoryItem
                        {
                            Id = c.Id.Value,
                            Name = c.Name,
                            Image = c.Image ?? ""
                        })
                        .ToList();
                }
            }
            catch
            {
                // 分类表不存在或数据库不可用时返回空集合，页面只显示"全部资源"
            }

            // 分类下的资源数量：pan_source 按 source_category_id 分组统计。
            // source_category_id 是 int 列，超出 int 范围的雪花 ID 不可能有归属资源，数量保持 0。
            if (list.Count > 0)
            {
                var counts = new PanSourceBLL().GetCategoryCounts().GetAwaiter().GetResult();
                if (counts != null && counts.Count > 0)
                {
                    foreach (var item in list)
                    {
                        if (item.Id > 0 && item.Id <= int.MaxValue
                            && counts.TryGetValue((int)item.Id, out int count))
                        {
                            item.Count = count;
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 资源分类页：左侧取 pan_source_category 分类列表，右侧按分类过滤 pan_source 资源，
        /// 在服务层统一映射为前台展示模型 ResourceItem，实体不进入 Controller/View。
        /// </summary>
        public CategoryPageModel GetCategory(int pageNo, int pageSize, long categoryId)
        {
            if (pageNo < 1) pageNo = 1;
            if (pageSize <= 0) pageSize = 20;
            if (categoryId < 0) categoryId = 0;

            var model = new CategoryPageModel
            {
                PageNo = pageNo,
                PageSize = pageSize,
                CurrentCategoryId = categoryId,
                HotList = BuildHotList()
            };

            // 左侧分类列（pan_source_category）：按 sort 升序，过滤后台禁用分类
            model.Categories = GetCategories();
            var current = model.Categories.FirstOrDefault(c => c.Id == categoryId);
            model.CurrentCategoryName = current != null ? current.Name : "";

            try
            {
                var obj = new PanSourceBLL().GetCategoryList(pageNo, pageSize, categoryId).GetAwaiter().GetResult();
                if (obj != null && obj.Tag == 1 && obj.Data != null)
                {
                    var categoryMap = BuildSourceCategoryMap();
                    model.Items = obj.Data.Select(e => ToResourceItem(e, categoryMap)).ToList();
                    model.TotalResult = obj.Total;
                }
            }
            catch
            {
                // pan_source 表不存在或数据库不可用时回退示例数据，保证页面可渲染
            }

            if (model.Items.Count == 0 && model.TotalResult == 0)
            {
                var fallback = categoryId > 0
                    ? _allItems.Where(i => i.SourceCategoryId.GetValueOrDefault() == categoryId).ToList()
                    : _allItems;
                model.TotalResult = fallback.Count;
                model.Items = fallback.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();
            }
            return model;
        }

        /// <summary>
        /// pan_source 实体 → 前台展示模型（仅在服务层内转换，避免实体字段泄漏到页面）
        /// </summary>
        private static ResourceItem ToResourceItem(PanSourceEntity entity, Dictionary<long, string> categoryMap)
        {
            string categoryName = "";
            if (entity.SourceCategoryId.HasValue && categoryMap != null)
            {
                categoryMap.TryGetValue(entity.SourceCategoryId.Value, out categoryName);
            }
            return new ResourceItem
            {
                Id = entity.Id.HasValue ? entity.Id.Value.ToString() : "",
                Title = entity.Title ?? "",
                Url = entity.Url ?? "",
                Code = entity.Code ?? "",
                Times = (entity.BaseModifyTime ?? entity.BaseCreateTime)?.ToString("yyyy-MM-dd HH:mm") ?? "",
                Src = "",
                Description = entity.Description ?? "",
                PageViews = entity.PageViews ?? 0,
                ImageUrl = entity.VodPic ?? "",
                VodContent = entity.VodContent ?? "",
                SourceCategoryId = entity.SourceCategoryId,
                CategoryName = categoryName ?? "",
                IsType = entity.IsType ?? 0
            };
        }

        /// <summary>
        /// 资源预览图列表：vod_pic 多图拆分（与后台编辑页 imageUpload 的分隔符 ; 一致，兼容 , |），
        /// 保持原顺序并去重，路径统一补齐前导 "/"；为空时回退 fallback。
        /// </summary>
        private static List<string> ImageUrlList(string imageUrl, string fallback = "")
        {
            var list = (imageUrl ?? "")
                .Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeImageUrl)
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList();
            if (list.Count == 0)
            {
                var fb = NormalizeImageUrl(fallback);
                if (fb.Length > 0)
                {
                    list.Add(fb);
                }
            }
            return list;
        }

        /// <summary>图片地址归一化：相对路径补前导 "/"，http(s)/data 原样返回。</summary>
        private static string NormalizeImageUrl(string url)
        {
            var s = (url ?? "").Trim();
            if (s.Length == 0)
            {
                return "";
            }
            if (!s.StartsWith("/")
                && !s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                s = "/" + s;
            }
            return s;
        }

        /// <summary>
        /// 分类ID → 分类名称 字典（pan_source_category 表），供列表页把 source_category_id 显示成文本
        /// </summary>
        private static Dictionary<long, string> BuildSourceCategoryMap()
        {
            var map = new Dictionary<long, string>();
            try
            {
                var obj = new PanSourceCategoryBLL().GetList(null).GetAwaiter().GetResult();
                if (obj != null && obj.Tag == 1 && obj.Data != null)
                {
                    foreach (var c in obj.Data)
                    {
                        if (c.Id.HasValue && !string.IsNullOrWhiteSpace(c.Name))
                        {
                            map[c.Id.Value] = c.Name;
                        }
                    }
                }
            }
            catch
            {
                // 分类表不存在或数据库不可用时忽略，分类文本留空
            }
            return map;
        }

        /// <summary>分类ID → 分类名称字典（搜索页左侧分类导航使用）。</summary>
        private Dictionary<string, string> BuildCategoryMap()
        {
            return new Dictionary<string, string>
            {
                { "1", "电影" }, { "2", "电视剧" }, { "3", "动漫" }, { "4", "综艺" }, { "5", "短剧" }
            };
        }

        private List<RankBlock> BuildHotList()
        {
            return new List<RankBlock>
            {
                new RankBlock { Name = "热门资源", List = _allItems.Take(10).ToList() },
                new RankBlock { Name = "最新更新", List = _allItems.Skip(5).Take(10).ToList() }
            };
        }

        private static List<ResourceItem> BuildSampleItems()
        {
            string[] titles =
            {
                "2026 最新高分电影合集（蓝光原盘）",
                "热播电视剧《星河长明》全集 1080P 夸克网盘",
                "经典动漫《海贼王》全剧场版合集 阿里云盘",
                "今日热门短剧《逆袭人生》全集",
                "精品纪录片《地球脉动》第四季 百度网盘",
                "学习资料：Python 从入门到精通（含源码）",
                "夸克网盘 精选影视资源包",
                "2026 春节档大片《热辣滚烫》1080P",
                "经典科幻《流浪地球》系列合集",
                "全网最火网剧《狂飙》高清全集",
                "高清壁纸与电脑桌面素材大合集",
                "无损音乐 FLAC 合集（顶级音质）"
            };
            var list = new List<ResourceItem>();
            for (int i = 0; i < titles.Length; i++)
            {
                list.Add(new ResourceItem
                {
                    Id = (i + 1).ToString(),
                    Title = titles[i],
                    Url = "https://pan.quark.cn/s/" + (100000 + i * 137).ToString(),
                    Code = "abcd" + i,
                    Times = "2026-10-" + (30 - i % 5).ToString("00") + " " + (i + 10).ToString("00") + ":" + (i * 3 % 60).ToString("00"),
                    IsType = i % 11,
                    Src = "",
                    VodContent = "本资源为聚合索引结果，点击下方按钮可直接访问网盘或复制链接保存。"
                });
            }
            return list;
        }
    }
}