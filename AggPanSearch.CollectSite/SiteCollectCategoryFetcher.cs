using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;
using HtmlAgilityPack;

namespace AggPanSearch.CollectSite;

/// <summary>抓取到的一个站点分类候选。</summary>
public sealed class SiteCategoryCandidate
{
    /// <summary>分类名称（链接文本）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分类页绝对地址。</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>站点分类抓取结果。</summary>
public sealed class SiteCategoryFetchResult
{
    /// <summary>是否成功拿到站点页面（抓取成功但没识别到分类时仍为 true）。</summary>
    public bool Success { get; set; }

    /// <summary>提示信息（失败原因或识别结果说明，中文可直接展示）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>实际访问的页面地址（重定向后）。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>分类候选列表（已去重、限条数）。</summary>
    public List<SiteCategoryCandidate> Items { get; set; } = new List<SiteCategoryCandidate>();
}

/// <summary>
/// 站点分类抓取器：抓站点首页，优先用已适配解析器（<see cref="ISiteCollectParser.ParseNav"/>）提取分类入口，
/// 未匹配到专用解析器时用通用兜底规则扫描导航链接。只做单次首页请求，纯只读。
/// </summary>
public static class SiteCollectCategoryFetcher
{
    /// <summary>分类候选上限，避免返回一屏装不下的数据。</summary>
    private const int MaxCandidates = 50;

    /// <summary>分类名称长度上限（过滤版权、长句导航）。</summary>
    private const int MaxNameLength = 20;

    /// <summary>路径/查询中常见的分类关键词。</summary>
    private static readonly Regex CategoryKeywordPattern = RegexCache.GetIgnoreCase(
        @"(category|categories|type|types|column|class|sort|list|tags?)(/|=|\?|&|$)");

    /// <summary>分页与杂项链接，不是分类。</summary>
    private static readonly Regex PagingPattern = RegexCache.GetIgnoreCase(
        @"(/page/\d+|[\?&](page|p)=\d+|\.(html?|php|jpe?g|png|gif|zip|rar|mp4)$)");

    /// <summary>纯数字或日期形式的文本，不是分类名。</summary>
    private static readonly Regex NumberOnlyPattern = RegexCache.Get(@"^\d{1,4}(-\d{1,2}){0,2}$");

    private static readonly HashSet<string> ExcludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "首页", "主页", "网站首页", "上一页", "下一页", "首页>", "末页", "尾页", "更多", "更多>>", "查看更多",
        "登录", "注册", "关于我们", "联系我们", "免责声明", "版权声明", "网站地图",
    };

    /// <summary>
    /// 抓取站点分类候选。抓取失败时 Success=false 并给出中文原因；
    /// 抓取成功但没有识别到分类时 Success=true、Items 为空，Message 提示手工添加。
    /// </summary>
    public static async Task<SiteCategoryFetchResult> FetchAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        SiteCategoryFetchResult result = new SiteCategoryFetchResult();
        if (!HtmlPageFetcher.IsAllowedUrl(siteUrl, out string urlError))
        {
            result.Message = urlError;
            return result;
        }

        PageFetchResult page = await HtmlPageFetcher
            .FetchAsync(siteUrl, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (!page.Success)
        {
            result.Message = string.IsNullOrWhiteSpace(page.Error) ? "抓取站点页面失败，请稍后重试" : page.Error;
            return result;
        }
        if (string.IsNullOrWhiteSpace(page.Html))
        {
            result.Message = "站点页面内容为空，无法识别分类";
            return result;
        }

        string finalUrl = string.IsNullOrWhiteSpace(page.FinalUrl) ? siteUrl : page.FinalUrl;
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out Uri baseUri))
        {
            result.Message = "站点链接格式不正确，无法识别分类";
            return result;
        }

        result.Success = true;
        result.SourceUrl = finalUrl;

        Dictionary<string, SiteCategoryCandidate> candidates = new Dictionary<string, SiteCategoryCandidate>(StringComparer.OrdinalIgnoreCase);

        // 1) 已适配站点的专用解析器（解析异常不影响后续兜底规则）
        try
        {
            SiteCollectNavResult nav = SiteCollectParserRegistry.Instance.Resolve(baseUri).ParseNav(page.Html, baseUri);
            foreach (SiteCollectNavLink link in nav.CategoryLinks)
            {
                AddCandidate(candidates, link.Name, link.Url, baseUri);
                if (candidates.Count >= MaxCandidates)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            candidates.Clear();
        }

        // 2) 通用兜底：扫描导航链接中的分类关键词
        if (candidates.Count == 0)
        {
            CollectFromAnchors(page.Html, baseUri, candidates);
        }

        result.Items = candidates.Values
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (result.Items.Count == 0)
        {
            result.Message = "未从站点页面识别到分类，请手工添加分类或检查站点链接";
        }
        else
        {
            result.Message = "已识别到 " + result.Items.Count + " 个分类";
        }
        return result;
    }

    /// <summary>通用兜底：扫描页面所有链接，文本像分类名且地址带分类关键词的收为候选。</summary>
    private static void CollectFromAnchors(string html, Uri baseUri, Dictionary<string, SiteCategoryCandidate> candidates)
    {
        HtmlDocument doc = SiteCollectHtmlUtil.LoadDocument(html);
        HtmlNodeCollection? anchors = doc.DocumentNode.SelectNodes("//a");
        if (anchors == null)
        {
            return;
        }
        foreach (HtmlNode anchor in anchors)
        {
            if (candidates.Count >= MaxCandidates)
            {
                return;
            }
            string text = SiteCollectHtmlUtil.CleanText(anchor.InnerText);
            if (!IsLikelyCategoryName(text))
            {
                continue;
            }
            string url = SiteCollectHtmlUtil.ResolveUrl(baseUri, anchor.GetAttributeValue("href", string.Empty));
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                continue;
            }
            // 只收同站链接，且地址需要带分类特征
            if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string pathAndQuery = uri.PathAndQuery;
            if (PagingPattern.IsMatch(pathAndQuery) || !CategoryKeywordPattern.IsMatch(pathAndQuery))
            {
                continue;
            }
            AddCandidate(candidates, text, url, baseUri);
        }
    }

    private static bool IsLikelyCategoryName(string? name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > MaxNameLength)
        {
            return false;
        }
        if (ExcludeNames.Contains(value))
        {
            return false;
        }
        // 纯符号、纯数字、纯日期都不是分类名
        if (!value.Any(char.IsLetter))
        {
            return false;
        }
        if (NumberOnlyPattern.IsMatch(value))
        {
            return false;
        }
        return true;
    }

    private static void AddCandidate(Dictionary<string, SiteCategoryCandidate> candidates, string? name, string? url, Uri baseUri)
    {
        string value = SiteCollectHtmlUtil.Truncate(SiteCollectHtmlUtil.CleanText(name), MaxNameLength);
        if (value.Length == 0 || !IsLikelyCategoryName(value))
        {
            return;
        }
        string absoluteUrl = SiteCollectHtmlUtil.ResolveUrl(baseUri, url);
        if (absoluteUrl.Length == 0)
        {
            return;
        }
        if (candidates.ContainsKey(absoluteUrl))
        {
            return;
        }
        candidates[absoluteUrl] = new SiteCategoryCandidate { Name = value, Url = absoluteUrl };
    }
}
