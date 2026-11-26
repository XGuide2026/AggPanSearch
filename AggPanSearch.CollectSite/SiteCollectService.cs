namespace AggPanSearch.CollectSite;

/// <summary>
/// 网页采集编排：地址校验 → 抓取 → 选择解析器 → 解析 → 列表页二次抓详情 → 链接识别/标准化/去重 → 字段裁剪。
/// 具体的模板规则、URL 规则、条目归一化分别由
/// <see cref="SiteCollectPageTemplate"/>、<see cref="SiteCollectUrlRules"/>、<see cref="SiteCollectItemNormalizer"/> 负责。
/// </summary>
public sealed class SiteCollectService
{
    /// <summary>单次采集最多跟进的详情页数量，避免一次抓取过多页面。</summary>
    private const int MaxDetailPages = 30;

    /// <summary>单次采集最多入库的资源条数。</summary>
    private const int MaxItems = 500;

    /// <summary>单次采集最多处理的列表页数量（未指定 maxPages 时的默认值）。</summary>
    private const int MaxListPages = 40;

    /// <summary>用户可指定的页数上限，防止输入过大导致长时间抓取。</summary>
    private const int MaxCustomPages = 100;

    /// <summary>列表页之间的间隔，降低被站点限流的概率。</summary>
    private const int PageIntervalMs = 200;

    private const int RetryDelayMs = 800;

    /// <summary>
    /// 按采集模式采集：
    /// 详情页采集（只抓取传入页面）、单个列表页采集（当前列表页 + 该页所有详情页）、
    /// 分页列表采集（按 pageUrlTemplate 的 {page} 占位符逐页采集）。
    /// 每处理完一个列表页通过 onPageCollected 交付本页条目，便于上层逐页入库并刷新进度。
    /// <paramref name="maxPages"/> 为本次采集的页数上限：分页列表采集限制列表页数，
    /// 单个列表页采集限制跟进的详情页数；小于等于 0 时使用引擎默认上限，详情页采集忽略该参数。
    /// <paramref name="startPage"/> 为分页列表采集的起始页码，从该页开始向后逐页采集，
    /// 小于等于 0 时回落为 1；其它模式忽略该参数。
    /// </summary>
    public async Task<SiteCollectResult> CollectAsync(
        string pageUrl,
        string pageUrlTemplate,
        SiteCollectMode mode,
        Func<SiteCollectedPage, CancellationToken, Task>? onPageCollected,
        Action<SiteCollectProgress>? onProgress,
        CancellationToken cancellationToken = default,
        int maxPages = 0,
        int startPage = 1)
    {
        int customPages = maxPages > 0 ? Math.Min(maxPages, MaxCustomPages) : 0;
        CollectContext context = new CollectContext
        {
            Mode = mode,
            OnPageCollected = onPageCollected,
            OnProgress = onProgress,
            Token = cancellationToken,
            ListPageLimit = mode == SiteCollectMode.PagedList && customPages > 0 ? customPages : MaxListPages,
            DetailPageLimit = mode == SiteCollectMode.SingleList && customPages > 0 ? customPages : MaxDetailPages,
        };

        if (!HtmlPageFetcher.IsAllowedUrl(pageUrl, out string urlError))
        {
            context.Result.Message = urlError;
            return context.Result;
        }

        if (mode == SiteCollectMode.PagedList)
        {
            if (!SiteCollectPageTemplate.TryParse(pageUrlTemplate, out SiteCollectPageTemplate template, out string templateError))
            {
                context.Result.Message = templateError;
                return context.Result;
            }
            await RunTemplateChainAsync(context, template, pageUrl, startPage > 0 ? startPage : 1).ConfigureAwait(false);
            return Complete(context);
        }

        PageOutcome outcome = await HandlePageAsync(context, pageUrl, SiteCollectUrlRules.GetSiteRoot(pageUrl)).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            context.Result.Message = outcome.Error;
        }
        return Complete(context);
    }

    /// <summary>
    /// 处理单个页面（列表页或详情页）：抓取解析、累计结果、回调进度与逐页入库回调。
    /// <paramref name="fallbackUrl"/> 仅用于首屏尾斜杠写法探测：主地址 404 时改试另一种写法，
    /// 命中则本页按成功处理。
    /// </summary>
    private async Task<PageOutcome> HandlePageAsync(
        CollectContext context,
        string url,
        string? referer,
        string? fallbackUrl = null)
    {
        SiteCollectResult result = context.Result;
        (bool ok, SiteCollectedPage? page, string error, int statusCode) = await ProcessPageAsync(context, url, referer).ConfigureAwait(false);

        bool usedFallback = false;
        if (!ok && statusCode == 404 && !string.IsNullOrWhiteSpace(fallbackUrl)
            && !string.Equals(fallbackUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            (bool altOk, SiteCollectedPage? altPage, string altError, _) = await ProcessPageAsync(context, fallbackUrl, referer)
                .ConfigureAwait(false);
            if (altOk && altPage != null)
            {
                ok = true;
                page = altPage;
                error = string.Empty;
                usedFallback = true;
            }
            else if (string.IsNullOrWhiteSpace(error))
            {
                error = altError;
            }
        }

        if (!ok || page == null)
        {
            result.ListPagesFailed++;
            context.Progress.Message = error;
            return new PageOutcome(false, error, 0, url, false);
        }

        result.ListPagesDone++;
        result.Items.AddRange(page.Items);
        result.Skipped += page.Skipped;

        context.Progress.Found = result.Items.Count;
        context.Progress.Skipped = result.Skipped;
        context.Progress.CurrentPageUrl = page.PageUrl;
        context.OnProgress?.Invoke(context.Progress);

        if (context.OnPageCollected != null && page.Items.Count > 0)
        {
            await context.OnPageCollected(page, context.Token).ConfigureAwait(false);
        }

        return new PageOutcome(true, string.Empty, page.Items.Count, page.PageUrl, usedFallback);
    }

    /// <summary>
    /// 按分页链接模板分两阶段采集，保证「已发现总条数」在任意入库前确定（进度条分母）：
    /// 阶段一扫描全部列表页，只解析、累加 <see cref="SiteCollectResult.TotalFound"/>，不抓详情不入库；
    /// 阶段二对扫描收集的条目统一逐条抓详情入库，进度 = 已入库 / 总条数，从 0 逐步爬升。
    /// {page} 从 startPage 开始递增，某页抓取失败或本页没有条目即视为已到末页；
    /// 首屏若因末尾斜杠写法不符返回 404，则自动改试另一种写法并在本次任务内沿用。
    /// </summary>
    private async Task RunTemplateChainAsync(
        CollectContext context,
        SiteCollectPageTemplate template,
        string basePageUrl,
        int startPage)
    {
        SiteCollectResult result = context.Result;
        // 首屏以站点根地址作 Referer，后续翻页以上一页作 Referer，模拟浏览器的站内导航链。
        string referer = SiteCollectUrlRules.GetSiteRoot(basePageUrl);
        int firstPage = startPage > 0 ? startPage : 1;
        int lastPage = firstPage + context.ListPageLimit - 1;
        bool trailingSlash = true;

        // ── 阶段一：扫描所有列表页，先确定总条数（只收集占位条目，不抓详情）──
        List<CollectedItem> scanned = new List<CollectedItem>();
        for (int pageNo = firstPage; pageNo <= lastPage; pageNo++)
        {
            context.Token.ThrowIfCancellationRequested();

            // 扫描阶段以「已扫描条目数」是否达到总量上限判断是否提前结束
            if (result.TotalFound >= MaxItems || result.ListPagesDone >= context.ListPageLimit)
            {
                result.LimitReached = true;
                break;
            }

            string currentUrl = template.BuildUrl(pageNo, trailingSlash);
            if (!HtmlPageFetcher.IsAllowedUrl(currentUrl, out string urlError))
            {
                result.Message = urlError;
                break;
            }
            if (!context.Visited.Add(SiteCollectUrlRules.GetPageDedupKey(currentUrl)))
            {
                break;
            }

            // 只在首屏探测一次斜杠写法，后续页沿用探测结果，避免额外请求。
            string? fallbackUrl = pageNo == firstPage && template.CanNormalizeSlash
                ? template.BuildUrl(pageNo, !trailingSlash)
                : null;

            PageOutcome outcome = await ScanListPageOutcomeAsync(context, currentUrl, referer, fallbackUrl, scanned).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                // 首页失败作为整体失败原因；后续页失败（常见为 404）视为已到末页。
                if (result.ListPagesDone == 0)
                {
                    result.Message = outcome.Error;
                }
                break;
            }

            if (outcome.UsedFallback)
            {
                trailingSlash = !trailingSlash;
            }

            // 超出范围的分页地址可能返回 200 的空白页或重复列表，识别为末页避免空转。
            if (outcome.ItemCount == 0 && pageNo > firstPage)
            {
                break;
            }

            referer = outcome.PageUrl.Length > 0 ? outcome.PageUrl : currentUrl;
            await Task.Delay(PageIntervalMs, context.Token).ConfigureAwait(false);
        }

        // ── 阶段二：统一逐条抓详情入库；此时分母 TotalFound 已固定 ──
        // 详情页上限放宽到单次采集上限，由 CollectFromListAsync 按剩余量自行收束。
        await CollectScannedAsync(context, scanned, basePageUrl, MaxItems).ConfigureAwait(false);
    }

    /// <summary>扫描单页并收集原始条目：抓取解析（含斜杠回退），成功则计入总条数并返回占位条目。</summary>
    private static async Task<PageOutcome> ScanListPageOutcomeAsync(
        CollectContext context,
        string url,
        string? referer,
        string? fallbackUrl,
        List<CollectedItem> scanned)
    {
        SiteCollectResult result = context.Result;
        (bool ok, SiteCollectParseResult? parsed, string finalUrl, string error, int statusCode) =
            await ScanListPageAsync(context, url, referer).ConfigureAwait(false);

        bool usedFallback = false;
        if (!ok && statusCode == 404 && !string.IsNullOrWhiteSpace(fallbackUrl)
            && !string.Equals(fallbackUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            (bool altOk, SiteCollectParseResult? altParsed, string altFinalUrl, string altError, _) =
                await ScanListPageAsync(context, fallbackUrl, referer).ConfigureAwait(false);
            if (altOk && altParsed != null)
            {
                ok = true;
                parsed = altParsed;
                finalUrl = altFinalUrl;
                error = string.Empty;
                usedFallback = true;
            }
            else if (string.IsNullOrWhiteSpace(error))
            {
                error = altError;
            }
        }

        if (!ok || parsed == null)
        {
            result.ListPagesFailed++;
            context.Progress.Message = error;
            return new PageOutcome(false, error, 0, url, false);
        }

        result.ListPagesDone++;
        context.Progress.Found = result.TotalFound;
        scanned.AddRange(parsed.Items);
        return new PageOutcome(true, string.Empty, parsed.Items.Count, finalUrl, usedFallback);
    }

    /// <summary>对扫描阶段收集的条目统一逐条抓详情入库（进度 = 已入库 / 已确定总条数）。</summary>
    private static async Task CollectScannedAsync(
        CollectContext context,
        List<CollectedItem> scanned,
        string listPageUrl,
        int maxDetailPages)
    {
        SiteCollectResult result = context.Result;
        if (scanned.Count == 0)
        {
            return;
        }

        int remaining = Math.Max(0, MaxItems - result.Items.Count);
        List<CollectedItem> raw = await CollectFromListAsync(context, scanned, remaining, listPageUrl, maxDetailPages, context.Token)
            .ConfigureAwait(false);

        SiteCollectItemNormalizeResult finalized = SiteCollectItemNormalizer.Normalize(raw, context.SeenUrls, remaining);
        if (finalized.Items.Count == 0)
        {
            return;
        }

        result.Items.AddRange(finalized.Items);
        result.Skipped += finalized.Skipped;

        context.Progress.Found = result.Items.Count;
        context.Progress.Skipped = result.Skipped;
        context.Progress.CurrentPageUrl = listPageUrl;
        context.OnProgress?.Invoke(context.Progress);

        if (context.OnPageCollected != null)
        {
            SiteCollectedPage part = new SiteCollectedPage
            {
                PageUrl = listPageUrl,
                Items = finalized.Items,
            };
            await context.OnPageCollected.Invoke(part, context.Token).ConfigureAwait(false);
        }
    }

    private static bool HasReachedLimit(CollectContext context)
    {
        return context.Result.Items.Count >= MaxItems || context.Result.ListPagesDone >= context.ListPageLimit;
    }

    /// <summary>汇总最终结果与提示信息。</summary>
    private static SiteCollectResult Complete(CollectContext context)
    {
        SiteCollectResult result = context.Result;
        if (result.Items.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(result.Message))
            {
                result.Message = result.ListPagesDone > 0 ? "采集到的条目均缺少有效网盘链接" : "未采集到有效资源";
            }
            return result;
        }

        result.Success = true;
        string message = string.Format("采集完成，发现 {0} 条有效资源", result.Items.Count);
        if (result.LimitReached)
        {
            message += "（已达到单次采集上限）";
        }
        if (result.ListPagesFailed > 0)
        {
            message += string.Format("，{0} 页采集失败", result.ListPagesFailed);
        }
        result.Message = message;
        return result;
    }

    /// <summary>抓取并解析一个页面（列表页或详情页），返回解析结果与最终地址；只做解析，不跟进详情、不计入结果。</summary>
    private static async Task<(bool Ok, SiteCollectParseResult? Parsed, string FinalUrl, string Error, int StatusCode)> FetchAndParseAsync(
        CollectContext context,
        string url,
        string? referer)
    {
        SiteCollectResult result = context.Result;
        PageFetchResult fetched = await FetchWithRetryAsync(url, referer, context.Token).ConfigureAwait(false);
        result.FetchedPages++;
        if (!fetched.Success)
        {
            return (false, null, string.Empty, fetched.Error, fetched.StatusCode);
        }

        string finalUrl = fetched.FinalUrl.Length > 0 ? fetched.FinalUrl : url;
        if (!SiteCollectHtmlUtil.TryCreateHttpUri(finalUrl, out Uri pageUri))
        {
            return (false, null, string.Empty, "采集网站地址无效", fetched.StatusCode);
        }

        ISiteCollectParser parser = SiteCollectParserRegistry.Instance.Resolve(pageUri);
        SiteCollectParseResult parsed = await parser
            .ParseAsync(fetched.Html, pageUri, context.Token)
            .ConfigureAwait(false);

        if (parsed.Status == SiteCollectParseStatus.StructureChanged)
        {
            return (false, null, finalUrl, string.IsNullOrWhiteSpace(parsed.Message)
                ? "页面结构无法识别，该站点可能需要适配解析器"
                : parsed.Message, fetched.StatusCode);
        }

        return (true, parsed, finalUrl, string.Empty, fetched.StatusCode);
    }

    /// <summary>
    /// 抓取并解析一个列表页，累计本次任务的目标总条数（TotalFound），返回本页原始条目与分页导航。
    /// 该方法是分页采集的「扫描阶段」：只确定分母，不抓详情、不入库。
    /// </summary>
    private static async Task<(bool Ok, SiteCollectParseResult? Parsed, string FinalUrl, string Error, int StatusCode)> ScanListPageAsync(
        CollectContext context,
        string url,
        string? referer)
    {
        SiteCollectResult result = context.Result;
        (bool ok, SiteCollectParseResult? parsed, string finalUrl, string error, int statusCode) =
            await FetchAndParseAsync(context, url, referer).ConfigureAwait(false);
        if (!ok || parsed == null)
        {
            return (false, null, finalUrl, error, statusCode);
        }

        // 先确定「已发现总条数」：累加本页解析出的条目数作为进度条分母，
        // 之后统一逐条入库，进度 = 已入库 / 总条数，从 0 逐步爬升。
        result.TotalFound += parsed.Items.Count;
        context.Progress.TotalFound = result.TotalFound;
        context.Progress.Found = result.Items.Count;
        context.Progress.CurrentPageUrl = finalUrl;
        context.OnProgress?.Invoke(context.Progress);
        return (true, parsed, finalUrl, string.Empty, statusCode);
    }

    /// <summary>抓取并解析一个列表页，返回本页过滤去重后的条目与分页导航。</summary>
    private static async Task<(bool Ok, SiteCollectedPage? Page, string Error, int StatusCode)> ProcessPageAsync(
        CollectContext context,
        string url,
        string? referer)
    {
        SiteCollectResult result = context.Result;
        (bool ok, SiteCollectParseResult? parsed, string finalUrl, string error, int statusCode) =
            await FetchAndParseAsync(context, url, referer).ConfigureAwait(false);
        if (!ok || parsed == null)
        {
            return (false, null, error, statusCode);
        }

        // 单页/详情页模式同理先确定本页「已发现条数」作分母，然后才逐条跟进详情入库。
        result.TotalFound += parsed.Items.Count;
        context.Progress.TotalFound = result.TotalFound;
        context.Progress.Found = result.Items.Count;
        context.Progress.CurrentPageUrl = finalUrl;
        context.OnProgress?.Invoke(context.Progress);

        int remaining = Math.Max(0, MaxItems - result.Items.Count);
        List<CollectedItem> raw = parsed.IsListPage
            ? await CollectFromListAsync(context, parsed.Items, remaining, finalUrl, context.DetailPageLimit, context.Token).ConfigureAwait(false)
            : parsed.Items;

        SiteCollectItemNormalizeResult finalized = SiteCollectItemNormalizer.Normalize(raw, context.SeenUrls, remaining);

        SiteCollectedPage page = new SiteCollectedPage
        {
            PageUrl = finalUrl,
            Items = finalized.Items,
            Skipped = finalized.Skipped,
            Nav = parsed.Nav,
        };
        return (true, page, string.Empty, statusCode);
    }

    /// <summary>
    /// 抓取失败时的重试策略：401/404 直接返回；
    /// 403/429 换备选 UA 并带 Referer 再试（最多两个备选 UA）；
    /// 网络异常/超时/5xx 间隔后原样重试一次。
    /// </summary>
    private static async Task<PageFetchResult> FetchWithRetryAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken)
    {
        PageFetchResult page = await HtmlPageFetcher
            .FetchAsync(url, referer, null, cancellationToken)
            .ConfigureAwait(false);
        if (page.Success || page.StatusCode == 401 || page.StatusCode == 404)
        {
            return page;
        }

        if (page.StatusCode == 403 || page.StatusCode == 429)
        {
            PageFetchResult retry = page;
            foreach (string userAgent in HtmlPageFetcher.FallbackUserAgents)
            {
                await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                retry = await HtmlPageFetcher
                    .FetchAsync(url, referer, userAgent, cancellationToken)
                    .ConfigureAwait(false);
                if (retry.Success || (retry.StatusCode != 403 && retry.StatusCode != 429))
                {
                    break;
                }
            }
            return retry;
        }

        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
        return await HtmlPageFetcher.FetchAsync(url, referer, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>分页采集上下文（结果、去重集合、进度与回调）。</summary>
    private sealed class CollectContext
    {
        public SiteCollectResult Result { get; } = new SiteCollectResult();

        public HashSet<string> Visited { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SeenUrls { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public SiteCollectProgress Progress { get; } = new SiteCollectProgress();

        public SiteCollectMode Mode { get; set; }

        /// <summary>本次采集允许处理的列表页数量上限（分页列表采集生效）。</summary>
        public int ListPageLimit { get; set; } = MaxListPages;

        /// <summary>单个列表页最多跟进的详情页数量上限。</summary>
        public int DetailPageLimit { get; set; } = MaxDetailPages;

        public Func<SiteCollectedPage, CancellationToken, Task>? OnPageCollected { get; set; }

        public Action<SiteCollectProgress>? OnProgress { get; set; }

        public CancellationToken Token { get; set; }
    }

    /// <summary>单个页面的处理结果：是否成功、失败原因、本页条目数、最终地址、是否走了斜杠回退。</summary>
    private readonly record struct PageOutcome(bool Ok, string Error, int ItemCount, string PageUrl, bool UsedFallback);

    /// <summary>
    /// 列表页：对每个条目跟进详情页，用详情解析结果补全网盘链接、简介与图片。
    /// </summary>
    private static async Task<List<CollectedItem>> CollectFromListAsync(
        CollectContext context,
        List<CollectedItem> placeholders,
        int remainingItems,
        string listPageUrl,
        int maxDetailPages,
        CancellationToken cancellationToken)
    {
        SiteCollectResult result = context.Result;
        List<CollectedItem> output = new List<CollectedItem>();
        output.AddRange(placeholders.Where(t => !t.NeedsDetail));

        int detailLimit = Math.Min(maxDetailPages, Math.Max(0, remainingItems));
        List<CollectedItem> targets = placeholders
            .Where(t => t.NeedsDetail)
            .Take(detailLimit)
            .ToList();
        if (targets.Count == 0)
        {
            return output;
        }

        List<CollectedItem>[] detailResults = new List<CollectedItem>[targets.Count];
        int fetchedPages = 0;

        // 详情页逐条串行采集：每抓完一条立即交付入库并刷新进度，
        // 让前端轮询能看到一条一条增长，而不是整页全部抓完后一次性跳变。
        for (int i = 0; i < targets.Count; i++)
        {
            await FetchDetailAsync(i).ConfigureAwait(false);

            List<CollectedItem>? merged = detailResults[i];
            if (merged == null || merged.Count == 0)
            {
                continue;
            }

            // 本详情页解析出的条目计入「已发现」，并立即刷新进度
            result.Items.AddRange(merged);
            context.Progress.Found = result.Items.Count;
            context.Progress.CurrentPageUrl = listPageUrl;
            context.OnProgress?.Invoke(context.Progress);

            // 立即交付本详情页条目入库（Runner 逐条 InsertBatch + Inserted++）
            if (context.OnPageCollected != null)
            {
                SiteCollectedPage part = new SiteCollectedPage
                {
                    PageUrl = listPageUrl,
                    Items = merged,
                };
                await context.OnPageCollected.Invoke(part, context.Token).ConfigureAwait(false);
            }
        }

        result.FetchedPages += fetchedPages;
        return output;

        async Task FetchDetailAsync(int index)
        {
            CollectedItem placeholder = targets[index];
            detailResults[index] = new List<CollectedItem>();
            try
            {
                // 详情页带所属列表页作 Referer，遇到 403/429 时沿用换 UA 重试策略。
                PageFetchResult detail = await FetchWithRetryAsync(placeholder.DetailUrl, listPageUrl, cancellationToken)
                    .ConfigureAwait(false);
                fetchedPages++;

                string detailUrl = detail.FinalUrl.Length > 0 ? detail.FinalUrl : placeholder.DetailUrl;
                if (!detail.Success || !SiteCollectHtmlUtil.TryCreateHttpUri(detailUrl, out Uri detailUri))
                {
                    return;
                }

                ISiteCollectParser detailParser = SiteCollectParserRegistry.Instance.Resolve(detailUri);
                SiteCollectParseResult detailParsed = await detailParser
                    .ParseAsync(detail.Html, detailUri, cancellationToken)
                    .ConfigureAwait(false);

                List<CollectedItem> merged = new List<CollectedItem>();
                foreach (CollectedItem link in detailParsed.Items)
                {
                    // 详情页的多图优先，取不到时回退列表页封面图
                    List<string> images = link.ImageUrls.Count > 0
                        ? link.ImageUrls
                        : (placeholder.ImageUrls.Count > 0
                            ? placeholder.ImageUrls
                            : SiteCollectHtmlUtil.SplitImages(placeholder.ImageUrl));
                    CollectedItem item = new CollectedItem
                    {
                        Name = placeholder.Name.Length > 0 ? placeholder.Name : link.Name,
                        // 简介只认详情页正文容器的解析结果，不用列表页卡片摘要兜底
                        Intro = link.Intro,
                        Url = link.Url,
                        Password = link.Password,
                        Category = link.Category.Length > 0 ? link.Category : placeholder.Category,
                        DetailUrl = placeholder.DetailUrl,
                        CloudType = link.CloudType,
                    };
                    item.SetImages(images);
                    merged.Add(item);
                }
                detailResults[index] = merged;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 单条详情抓取失败不影响整批采集，后面按「跳过」统计
            }
            finally
            {
                try
                {
                    // 每处理完一条详情页暂停 1000ms 再处理下一条，成功与失败路径均生效
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 取消时立即退出，不等待延时
                }
            }
        }
    }
}
