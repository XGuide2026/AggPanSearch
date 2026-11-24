using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Models;
using AggPanSearch.Business.PanManage;
using AggPanSearch.Entity.PanManage;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Services
{
    /// <summary>
    /// 用户前台站点地图服务：生成 sitemap.xml、robots.txt，并为可视化站点地图页提供分类数据。
    /// 
    /// 设计要点：
    /// 1. 详情页数据持续增长，因此不用 wwwroot 静态文件，改为运行时生成；
    /// 2. 生成成本高（pan_source 需分批全量枚举），结果做进程内静态缓存，
    ///    爬虫高并发访问时只有第一个线程真正查询数据库，其余线程复用快照；
    /// 3. 任何数据异常都降级为「固定栏目 + 分类」，最差也要返回合法 XML，
    ///    避免站点地图 5xx 导致搜索引擎降低抓取频率。
    /// </summary>
    public class SitemapService
    {
        /// <summary>单个 sitemap 文件的 URL 上限（搜索引擎规范为 50000 条）。超出后需拆分为 sitemapindex 分片。</summary>
        private const int MaxUrlCount = 50000;

        /// <summary>详情页分批枚举的每批条数：过大单次查询耗时长，过小查询次数多。</summary>
        private const int DetailPageSize = 1000;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan FailCacheDuration = TimeSpan.FromMinutes(5);
        private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

        // 进程内快照（按 baseUrl 区分）：域名变更后自然失效
        private static readonly object CacheLock = new object();
        private static string _cachedXml;
        private static string _cachedKey;
        private static DateTime _cachedExpire = DateTime.MinValue;

        private readonly IUserFrontendService _frontendService;

        public SitemapService(IUserFrontendService frontendService)
        {
            _frontendService = frontendService;
        }

        /// <summary>
        /// 生成 sitemap.xml 文本。baseUrl 形如 https://example.com（不带结尾斜杠）。
        /// </summary>
        public string BuildSitemapXml(string baseUrl)
        {
            string key = NormalizeBaseUrl(baseUrl);

            lock (CacheLock)
            {
                if (_cachedXml != null && string.Equals(_cachedKey, key, StringComparison.Ordinal) && DateTime.Now < _cachedExpire)
                {
                    return _cachedXml;
                }
            }

            bool degraded = false;
            List<SitemapUrl> urls;
            try
            {
                urls = BuildUrls();
            }
            catch
            {
                // 分类/资源表不存在或数据库不可用：只输出固定栏目，保证 XML 合法
                degraded = true;
                urls = BuildStaticUrls();
            }

            string xml = ComposeXml(key, urls);

            lock (CacheLock)
            {
                _cachedKey = key;
                _cachedXml = xml;
                // 降级结果只缓存 5 分钟，故障恢复后能较快产出完整地图
                _cachedExpire = DateTime.Now.Add(degraded ? FailCacheDuration : CacheDuration);
            }
            return xml;
        }

        /// <summary>
        /// 生成 robots.txt 文本：屏蔽搜索页与后台管理路径，并声明站点地图绝对地址。
        /// </summary>
        public string BuildRobotsTxt(string baseUrl)
        {
            string root = NormalizeBaseUrl(baseUrl);

            // 搜索页是无限 URL 空间（任意关键词都能生成一个页面），后台 Area 与附件目录不对访客开放，
            // 这些路径抓取只会消耗抓取预算，需明确屏蔽。
            string[] disallowPaths =
            {
                "/s/",
                "/search",
                "/UserFrontend/Home/Search",
                "/api/",
                "/Resource/",
                "/PanManage/",
                "/SystemManage/",
                "/OrganizationManage/",
                "/ToolManage/",
                "/DemoManage/"
            };

            var sb = new StringBuilder();
            sb.AppendLine("User-agent: *");
            foreach (string path in disallowPaths)
            {
                sb.AppendLine("Disallow: " + path);
            }
            sb.AppendLine("Allow: /");
            sb.AppendLine();
            sb.AppendLine("Sitemap: " + root + "/sitemap.xml");
            return sb.ToString();
        }

        /// <summary>
        /// 可视化站点地图页所需的分类列表（已按 sort 排序、过滤禁用分类，并带资源数量）。
        /// </summary>
        public List<CategoryItem> GetCategoriesForPage()
        {
            try
            {
                return _frontendService.GetCategories() ?? new List<CategoryItem>();
            }
            catch
            {
                return new List<CategoryItem>();
            }
        }

        /// <summary>
        /// 组装全部条目：固定栏目 → 各分类列表页 → 全量资源详情页。
        /// 分类和详情页分别 try，单块失败不影响其他块输出。
        /// </summary>
        private List<SitemapUrl> BuildUrls()
        {
            DateTime now = DateTime.Now;
            List<SitemapUrl> urls = BuildStaticUrls();

            // 每个分类对应一个带参数的可访问地址，分类主键是 bigint，必须按 long 输出
            try
            {
                foreach (CategoryItem category in _frontendService.GetCategories())
                {
                    if (category == null || category.Id <= 0)
                    {
                        continue;
                    }
                    urls.Add(new SitemapUrl(
                        "/category?categoryId=" + category.Id.ToString(CultureInfo.InvariantCulture),
                        now,
                        "weekly",
                        "0.7"));
                }
            }
            catch
            {
                // 分类表不可用：跳过分类条目
            }

            // 资源详情页：分批枚举，达到 URL 上限即停止
            try
            {
                var bll = new PanSourceBLL();
                int pageNo = 1;
                while (urls.Count < MaxUrlCount)
                {
                    var obj = bll.GetCategoryList(pageNo, DetailPageSize, 0).GetAwaiter().GetResult();
                    if (obj == null || obj.Tag != 1 || obj.Data == null || obj.Data.Count == 0)
                    {
                        break;
                    }

                    bool reachedLimit = false;
                    foreach (PanSourceEntity entity in obj.Data)
                    {
                        if (urls.Count >= MaxUrlCount)
                        {
                            reachedLimit = true;
                            break;
                        }
                        if (entity == null || !entity.Id.HasValue || entity.Id.Value <= 0)
                        {
                            continue;
                        }
                        urls.Add(new SitemapUrl(
                            "/d/" + entity.Id.Value.ToString(CultureInfo.InvariantCulture) + ".html",
                            ResolveLastMod(entity),
                            "weekly",
                            "0.6"));
                    }
                    if (reachedLimit)
                    {
                        break;
                    }

                    // 取完最后一页（数据不足一页，或已翻到 Total 之后）即结束
                    if (obj.Data.Count < DetailPageSize)
                    {
                        break;
                    }
                    if (obj.Total > 0 && (long)pageNo * DetailPageSize >= obj.Total)
                    {
                        break;
                    }
                    pageNo++;
                }
            }
            catch
            {
                // pan_source 表不存在或数据库不可用：只输出栏目与分类
            }

            return urls;
        }

        /// <summary>固定栏目页：始终可输出的兜底条目。</summary>
        private static List<SitemapUrl> BuildStaticUrls()
        {
            DateTime now = DateTime.Now;
            return new List<SitemapUrl>
            {
                new SitemapUrl("/", now, "daily", "1.0"),
                new SitemapUrl("/category", now, "daily", "0.9"),
                new SitemapUrl("/copyright", now, "monthly", "0.2"),
                new SitemapUrl("/banned_words", now, "monthly", "0.2")
            };
        }

        /// <summary>
        /// 用 XDocument 生成 XML：由框架负责命名空间与转义（分类 URL 里的 &amp; 会被正确转义）。
        /// </summary>
        private static string ComposeXml(string baseUrl, List<SitemapUrl> urls)
        {
            var root = new XElement(SitemapNs + "urlset");
            foreach (SitemapUrl url in urls)
            {
                var node = new XElement(SitemapNs + "url",
                    new XElement(SitemapNs + "loc", baseUrl + url.Path));

                // 时间无效（数据库默认值）时不输出 lastmod，交给搜索引擎自行判断
                string lastMod = FormatLastMod(url.LastMod);
                if (!string.IsNullOrEmpty(lastMod))
                {
                    node.Add(new XElement(SitemapNs + "lastmod", lastMod));
                }
                node.Add(
                    new XElement(SitemapNs + "changefreq", url.ChangeFreq),
                    new XElement(SitemapNs + "priority", url.Priority));
                root.Add(node);
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
            using (var writer = new Utf8StringWriter())
            {
                doc.Save(writer, SaveOptions.None);
                return writer.ToString();
            }
        }

        /// <summary>
        /// 详情页的最后更新时间：优先取修改时间，为空时退回创建时间。
        /// 数据库默认日期是 1970-01-01，转成 UTC 后会变成 1969-12-31，属明显错误数据，这里直接返回 null 不输出。
        /// </summary>
        private static DateTime? ResolveLastMod(PanSourceEntity entity)
        {
            DateTime? candidate = entity.BaseModifyTime ?? entity.BaseCreateTime;
            if (candidate.HasValue && candidate.Value.Year > 1970)
            {
                return candidate.Value;
            }
            return null;
        }

        /// <summary>W3C 日期格式（UTC），搜索引擎统一按此解析 lastmod；无有效时间时返回 null。</summary>
        private static string FormatLastMod(DateTime? time)
        {
            if (!time.HasValue)
            {
                return null;
            }

            DateTime value = time.Value;
            if (value.Kind == DateTimeKind.Unspecified)
            {
                // 数据库读出的是本地时间，按本地时间转换
                value = DateTime.SpecifyKind(value, DateTimeKind.Local);
            }
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>去掉结尾斜杠，避免拼出 //sitemap.xml；空值时不加前缀（仅本地调试场景）。</summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        /// <summary>StringWriter 默认声明为 UTF-16，这里固定为 UTF-8，让 XML 声明与实际输出编码一致。</summary>
        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }

        /// <summary>站点地图中的单条 URL。</summary>
        private sealed class SitemapUrl
        {
            public SitemapUrl(string path, DateTime? lastMod, string changeFreq, string priority)
            {
                Path = path;
                LastMod = lastMod;
                ChangeFreq = changeFreq;
                Priority = priority;
            }

            public string Path { get; }
            public DateTime? LastMod { get; }
            public string ChangeFreq { get; }
            public string Priority { get; }
        }
    }
}
