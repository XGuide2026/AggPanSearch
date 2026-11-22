using System;
using System.Collections.Generic;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：站点分类 → 「分类资源」Id 的映射快照。
    /// 采集任务启动时按站点的「配置分类」构建一次，后台线程只读该快照，
    /// 避免任务运行期回读 collect-sites.json（文件 IO + 并发风险）。
    /// 匹配规则：
    ///   1. 分类名称（忽略大小写与空白，完全相等）；
    ///   2. 分类页地址（去 query/fragment 与尾部斜杠后完全相等）；
    ///   3. 都未命中返回 null，由调用方写成「未分类」。
    /// 未绑定分类资源（SourceCategoryId &lt;= 0）的分类项不参与映射。
    /// </summary>
    public sealed class PanSourceCollectCategoryMap
    {
        private static readonly PanSourceCollectCategoryMap Empty = new PanSourceCollectCategoryMap();

        private PanSourceCollectCategoryMap()
        {
            NameMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            UrlMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>分类名称（规范化后）→ 分类资源 Id。</summary>
        private Dictionary<string, long> NameMap { get; }

        /// <summary>分类页地址（规范化后）→ 分类资源 Id。</summary>
        private Dictionary<string, long> UrlMap { get; }

        /// <summary>
        /// 按站点分类配置构建映射；同样的名称/地址出现多次时以配置顺序第一个已绑定项为准。
        /// </summary>
        public static PanSourceCollectCategoryMap Build(List<PanSourceCollectSiteCategory> categories)
        {
            if (categories == null || categories.Count == 0)
            {
                return Empty;
            }

            PanSourceCollectCategoryMap map = new PanSourceCollectCategoryMap();
            foreach (PanSourceCollectSiteCategory item in categories)
            {
                if (item == null || item.SourceCategoryId <= 0)
                {
                    continue;
                }

                string nameKey = NormalizeName(item.Name);
                if (nameKey.Length > 0 && !map.NameMap.ContainsKey(nameKey))
                {
                    map.NameMap[nameKey] = item.SourceCategoryId;
                }

                string urlKey = NormalizeUrl(item.Url);
                if (urlKey.Length > 0 && !map.UrlMap.ContainsKey(urlKey))
                {
                    map.UrlMap[urlKey] = item.SourceCategoryId;
                }
            }
            return map;
        }

        /// <summary>
        /// 换算一条采集结果的分类资源 Id：分类名优先，分类页地址兜底，未命中返回 null。
        /// </summary>
        /// <param name="siteCategoryName">源站分类名（解析器识别或本次采集的覆盖值）。</param>
        /// <param name="pageUrl">当前采集的列表页地址，用于地址兜底匹配。</param>
        public long? Resolve(string? siteCategoryName, string? pageUrl)
        {
            string nameKey = NormalizeName(siteCategoryName);
            if (nameKey.Length > 0 && NameMap.TryGetValue(nameKey, out long byName))
            {
                return byName;
            }

            string urlKey = NormalizeUrl(pageUrl);
            if (urlKey.Length > 0 && UrlMap.TryGetValue(urlKey, out long byUrl))
            {
                return byUrl;
            }
            return null;
        }

        /// <summary>分类名规范化：去掉全部空白字符并忽略大小写。</summary>
        internal static string NormalizeName(string? value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            // 站点的分类链接文本常带换行/全角空格，按字符过滤比 Replace 更稳
            char[] buffer = new char[text.Length];
            int length = 0;
            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    buffer[length++] = ch;
                }
            }
            return new string(buffer, 0, length).ToLowerInvariant();
        }

        /// <summary>
        /// 分类页地址规范化：只保留「协议 + 主机 + 端口 + 路径」，去掉 query/fragment 与尾部斜杠。
        /// 非法地址退化为去空白、去尾部斜杠的小写文本，避免异常中断采集。
        /// </summary>
        internal static string NormalizeUrl(string? value)
        {
            string url = (value ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                return string.Empty;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return url.TrimEnd('/').ToLowerInvariant();
            }

            string path = uri.AbsolutePath.TrimEnd('/');
            string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
            return (uri.Scheme + "://" + uri.Host + port + path).ToLowerInvariant();
        }
    }
}
