namespace AggPanSearch.CollectSite;

/// <summary>
/// 网页采集到的一条网盘资源。
/// </summary>
public sealed class CollectedItem
{
    /// <summary>资源名称（详情页标题或列表项标题）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>资源简介。</summary>
    public string Intro { get; set; } = string.Empty;

    /// <summary>网盘分享链接（已标准化）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>提取码 / 访问码（没有则为空）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>分类（优先取源站栏目/面包屑）。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>图片地址（多张用英文分号分隔，与 <see cref="ImageUrls"/> 保持一致）。</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>图片地址列表（多图，最多 5 张，首图作封面）。</summary>
    public List<string> ImageUrls { get; set; } = new List<string>();

    /// <summary>本条资源所在详情页地址；列表页解析时用于二次抓取详情。</summary>
    public string DetailUrl { get; set; } = string.Empty;

    /// <summary>网盘类型（由 NetDiskDetector 识别后回填）。</summary>
    public string CloudType { get; set; } = string.Empty;

    /// <summary>是否还需要抓取详情页才能拿到网盘链接。</summary>
    public bool NeedsDetail => string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(DetailUrl);

    /// <summary>设置多图：去空、去重、限 5 张，并同步写回 <see cref="ImageUrl"/>。</summary>
    public void SetImages(IEnumerable<string>? images)
    {
        ImageUrls = (images ?? Enumerable.Empty<string>())
            .Select(image => (image ?? string.Empty).Trim())
            .Where(image => image.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SiteCollectHtmlUtil.MaxImageCount)
            .ToList();
        ImageUrl = string.Join(";", ImageUrls);
    }

    public CollectedItem Clone()
    {
        return new CollectedItem
        {
            Name = Name,
            Intro = Intro,
            Url = Url,
            Password = Password,
            Category = Category,
            ImageUrl = ImageUrl,
            ImageUrls = new List<string>(ImageUrls),
            DetailUrl = DetailUrl,
            CloudType = CloudType,
        };
    }
}

/// <summary>解析状态。</summary>
public enum SiteCollectParseStatus
{
    Ok,
    /// <summary>页面结构可识别，但没有任何资源条目。</summary>
    NoItems,
    /// <summary>页面结构与预期不符（站点改版、需要登录、依赖 JS 渲染等）。</summary>
    StructureChanged,
}

/// <summary>单个页面的解析结果。</summary>
public sealed class SiteCollectParseResult
{
    public SiteCollectParseStatus Status { get; set; } = SiteCollectParseStatus.Ok;

    /// <summary>解析出的条目（列表页的条目可能只有 DetailUrl，需要二次抓取详情）。</summary>
    public List<CollectedItem> Items { get; set; } = new();

    /// <summary>当前页面是否为列表页。</summary>
    public bool IsListPage { get; set; }

    /// <summary>分页导航信息（下一页、总页数、分类入口），由解析器在解析时填充。</summary>
    public SiteCollectNavResult Nav { get; set; } = new SiteCollectNavResult();

    public string Message { get; set; } = string.Empty;
}

/// <summary>一次采集（抓取 + 解析）的整体结果。</summary>
public sealed class SiteCollectResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<CollectedItem> Items { get; set; } = new();

    /// <summary>解析出的资源条数（去重前）。</summary>
    public int Found => Items.Count;

    /// <summary>累计解析出的条目总数（列表页解析时即累加，先于入库确定，作进度条分母）。</summary>
    public int TotalFound { get; set; }

    /// <summary>被跳过的条目数（无链接、链接类型不识别、字段超长等）。</summary>
    public int Skipped { get; set; }

    /// <summary>实际抓取的页面数（列表页 + 详情页）。</summary>
    public int FetchedPages { get; set; }

    /// <summary>已处理的列表页数量。</summary>
    public int ListPagesDone { get; set; }

    /// <summary>抓取或解析失败的列表页数量。</summary>
    public int ListPagesFailed { get; set; }

    /// <summary>是否因页数（40 页）或条数（500 条）上限提前停止。</summary>
    public bool LimitReached { get; set; }
}

/// <summary>采集模式。</summary>
public enum SiteCollectMode
{
    /// <summary>详情页采集：只抓取传入的详情页，采集单条资源信息。</summary>
    DetailPage = 0,

    /// <summary>单个列表页采集：抓取当前列表页，并进入该页所有详情页取链接。</summary>
    SingleList = 1,

    /// <summary>分页列表采集：按分页链接的 {page} 占位符逐页采集列表及其详情页。</summary>
    PagedList = 2,
}

/// <summary>分页导航中的一条链接（分类入口等）。</summary>
public sealed class SiteCollectNavLink
{
    public string Url { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>页面解析出的分页导航信息。</summary>
public sealed class SiteCollectNavResult
{
    /// <summary>下一页绝对地址，空表示没有下一页。</summary>
    public string NextPageUrl { get; set; } = string.Empty;

    /// <summary>页面提示的总页数（如「共16页」），0 表示未知。</summary>
    public int TotalPagesHint { get; set; }

    /// <summary>分类入口链接（仅全站采集使用）。</summary>
    public List<SiteCollectNavLink> CategoryLinks { get; set; } = new List<SiteCollectNavLink>();
}

/// <summary>一个列表页解析并转成有效资源后的结果。</summary>
public sealed class SiteCollectedPage
{
    /// <summary>本页地址。</summary>
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>本页过滤去重后的有效资源。</summary>
    public List<CollectedItem> Items { get; set; } = new List<CollectedItem>();

    /// <summary>本页被跳过的条目数。</summary>
    public int Skipped { get; set; }

    /// <summary>本页解析出的分页导航信息。</summary>
    public SiteCollectNavResult Nav { get; set; } = new SiteCollectNavResult();
}

/// <summary>采集进度（引擎每处理完一个阶段刷新一次）。</summary>
public sealed class SiteCollectProgress
{
    /// <summary>累计发现的资源条数（含未入库的列表条目）。</summary>
    public int Found { get; set; }

    /// <summary>预计/累计解析出的条目总数（进度条分母，优先于 Found 使用）。</summary>
    public int TotalFound { get; set; }

    /// <summary>累计跳过条数。</summary>
    public int Skipped { get; set; }

    /// <summary>当前处理的页面地址。</summary>
    public string CurrentPageUrl { get; set; } = string.Empty;

    /// <summary>最近一次提示信息（失败页原因等）。</summary>
    public string Message { get; set; } = string.Empty;
}
