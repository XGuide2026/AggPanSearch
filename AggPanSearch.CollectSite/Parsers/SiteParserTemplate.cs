using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite.Parsers;

/// <summary>
/// 站点解析器模板：新增一个资源站时复制本文件，改类名、Name、优先级、域名与正则即可。
/// <para>接入步骤：</para>
/// <para>1. 把 <see cref="CanHandle"/> 里的域名改成目标站点；</para>
/// <para>2. 用浏览器 F12 观察列表页条目容器与详情页正文结构，替换下面的正则；</para>
/// <para>3. 在 <see cref="SiteCollectParserRegistry"/> 构造函数中注册本解析器；</para>
/// <para>4. 列表页只需返回带 DetailUrl 的条目（Url 留空），采集编排层会自动抓详情页补链接。</para>
/// 未适配的站点无需写解析器，注册表会自动回退到 <see cref="GenericNetdiskSiteParser"/>。
/// </summary>
public sealed class SiteParserTemplate : ISiteCollectParser
{
    private const string SiteHost = "example.com";

    public string Name => "site-template";

    public int Priority => 100;

    public bool CanHandle(Uri pageUri)
    {
        return pageUri.Host.Contains(SiteHost, StringComparison.OrdinalIgnoreCase);
    }

    // 列表页：条目块（起始标签里应包含详情页 href 与标题 title）
    private static readonly Regex ListItemPattern = RegexCache.Get(
        @"<a\b[^>]*class=[""'][^""']*item-card[^""']*[""'][^>]*>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HrefPattern = RegexCache.GetIgnoreCase(@"\bhref=[""'](?<h>[^""']+)[""']");

    private static readonly Regex TitleAttrPattern = RegexCache.GetIgnoreCase(@"\btitle=[""'](?<t>[^""']*)[""']");

    // 详情页：标题与正文容器
    private static readonly Regex DetailTitlePattern = RegexCache.Get(
        @"<h1\b[^>]*class=[""'][^""']*article-title[^""']*[""'][^>]*>(?<t>.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public Task<SiteCollectParseResult> ParseAsync(string html, Uri pageUri, CancellationToken cancellationToken = default)
    {
        SiteCollectParseResult result = new SiteCollectParseResult();

        Match title = DetailTitlePattern.Match(html);
        string name = title.Success
            ? SiteCollectHtmlUtil.CleanText(SiteCollectHtmlUtil.TagPatternForText(title.Groups["t"].Value))
            : string.Empty;

        string plainText = SiteCollectHtmlUtil.ToPlainText(html);
        foreach (Candidate link in NetDiskDetector.ExtractNetDiskLinks(plainText))
        {
            result.Items.Add(new CollectedItem
            {
                Name = name,
                Url = link.Url,
                Password = NetDiskDetector.ExtractPassword(plainText, link.Url),
                Category = string.Empty,
                DetailUrl = pageUri.AbsoluteUri,
                CloudType = link.Type,
            });
        }

        result.Status = result.Items.Count > 0
            ? SiteCollectParseStatus.Ok
            : SiteCollectParseStatus.NoItems;
        return Task.FromResult(result);
    }
}
