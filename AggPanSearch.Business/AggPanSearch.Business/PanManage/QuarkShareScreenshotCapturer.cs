using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：夸克分享页移动端截图器（Playwright 无头 Chromium）。
    /// 以手机视口（360x878、DPR 3、Android UA、触摸）打开分享链接：首个条目是文件夹则点进去，
    /// 首个条目是文件 / 压缩包则停在根目录；从内容表头（.ShareDetail--content-header）顶部向下截到
    /// 文件列表前 8 个 file-item 结束，不足 8 个按 8 个高度预留底部留白；
    /// 不包含顶部「打开 APP / 分享人 / 文件总数 / 容量 / 举报」区域。
    /// switchToThumbnail=true 时在进入文件夹后先点击视图切换按钮（span.ShareDetail--header-mode--*），
    /// 把列表视图切换为缩略图（网格）视图再截图，用于「打开文件夹显示文件缩略图截图」模式。
    /// 浏览器进程在进程内复用，任务内串行调用；只返回 PNG 字节，落盘与入库由调用方负责。
    /// </summary>
    public static class QuarkShareScreenshotCapturer
    {
        public const string ErrorNotQuark = "仅支持夸克资源";
        public const string ErrorInvalidUrl = "资源链接不正确";
        public const string ErrorTimeout = "页面加载超时";
        public const string ErrorPassword = "需要提取码";
        public const string ErrorShareGone = "分享链接已失效";
        public const string ErrorBrowserMissing = "服务器未安装 Chromium（请执行 playwright install chromium）";

        /// <summary>移动端视口宽高（CSS 像素），与前台 H5 页保持一致；
        /// 高度为 732 上浮 20%（732 × 1.2 ≈ 878），让文件列表第 8 个条目更完整地落入视口。</summary>
        private const int ViewportWidth = 360;
        private const int ViewportHeight = 878;

        /// <summary>页面跳转超时：夸克 H5 为 SPA，DOMContentLoaded 之后还需等待列表渲染。</summary>
        private const int NavigationTimeoutMs = 25000;

        /// <summary>等待文件列表渲染的超时。</summary>
        private const int ListTimeoutMs = 12000;

        /// <summary>点击条目进入文件夹后，等待列表切换的超时。</summary>
        private const int EnterFolderTimeoutMs = 10000;

        /// <summary>条目列表选择器：夸克的 class 带构建哈希，这里只用稳定的前缀类名。</summary>
        private const string ListItemSelector = ".mini-file-list .file-item";

        /// <summary>列表定位失败时按表头下这一固定高度保守裁剪，避免整视口把列表下方无关页面一起截进来。</summary>
        private const double FallbackClipHeight = 620;

        /// <summary>列表 / 缩略图（网格）视图切换按钮：夸克哈希类名 ShareDetail--header-mode--xxx，
        /// 只用含页面级稳定前缀的 span 选择器；点击后把文件列表从列表视图切换为缩略图视图。</summary>
        private const string ViewModeSwitchSelector = "span[class*=\"ShareDetail--header-mode\"]";

        /// <summary>Android Chrome 移动端 UA，让分享页走 H5 版（路由 #/list/share）。</summary>
        private const string MobileUserAgent =
            "Mozilla/5.0 (Linux; Android 16; Pixel 10) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/149.0.0.0 Mobile Safari/537.36";

        private static readonly SemaphoreSlim BrowserLock = new SemaphoreSlim(1, 1);
        private static IPlaywright s_playwright;
        private static IBrowser s_browser;

        /// <summary>
        /// 截图并返回 PNG 字节与失败原因（成功时 Error 为空）。
        /// switchToThumbnail=true 时在进入文件夹后先点击视图模式切换按钮，再截图（缩略图截图模式）。
        /// 失败原因均为可直接展示给用户的中文短句。
        /// </summary>
        public static async Task<(byte[] Bytes, string Error)> CaptureAsync(
            string shareUrl, string code, CancellationToken token, bool switchToThumbnail = false)
        {
            if (!TryNormalizeUrl(shareUrl, code, out string targetUrl, out string urlError))
            {
                return (null, urlError);
            }

            token.ThrowIfCancellationRequested();

            IBrowser browser;
            try
            {
                browser = await GetBrowserAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (null, DescribeException(ex));
            }

            IBrowserContext context = null;
            IPage page = null;
            try
            {
                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
                    DeviceScaleFactor = 3,
                    IsMobile = true,
                    HasTouch = true,
                    UserAgent = MobileUserAgent,
                    Locale = "zh-CN",
                }).ConfigureAwait(false);

                page = await context.NewPageAsync().ConfigureAwait(false);
                page.SetDefaultTimeout(ListTimeoutMs);

                await page.GotoAsync(targetUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = NavigationTimeoutMs,
                }).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                // 等待 SPA 把文件列表渲染出来，渲染不出来多半是提取码 / 失效 / 网络问题
                bool listReady = await WaitForListAsync(page).ConfigureAwait(false);
                if (!listReady)
                {
                    return (null, await DetectBlockReasonAsync(page).ConfigureAwait(false));
                }

                token.ThrowIfCancellationRequested();

                // 首个条目是文件夹时进入；压缩包 / 普通文件停在根目录直接截
                await TryEnterFirstFolderAsync(page).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                // 缩略图截图模式：先点击视图切换按钮把列表切换为缩略图视图，再截图
                if (switchToThumbnail)
                {
                    await TrySwitchToThumbnailViewAsync(page).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                byte[] bytes = await TakeScreenshotAsync(page).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                {
                    return (null, "截图内容为空");
                }
                return (bytes, string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Playwright 超时在不同版本可能以 PlaywrightException 或 TimeoutException 等类型抛出，
                // 统一转成可展示的中文原因，避免底层英文超时文本穿透到任务层
                return (null, DescribeException(ex));
            }
            finally
            {
                if (page != null)
                {
                    try { await page.CloseAsync().ConfigureAwait(false); } catch { }
                }
                if (context != null)
                {
                    try { await context.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }

        /// <summary>浏览器进程复用：任务内串行调用，进程退出时由系统回收。</summary>
        private static async Task<IBrowser> GetBrowserAsync(CancellationToken token)
        {
            if (s_browser != null && s_browser.IsConnected)
            {
                return s_browser;
            }

            await BrowserLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (s_browser != null && s_browser.IsConnected)
                {
                    return s_browser;
                }
                if (s_browser != null)
                {
                    // 浏览器进程异常退出后重建
                    try { await s_browser.DisposeAsync().ConfigureAwait(false); } catch { }
                    s_browser = null;
                }
                if (s_playwright == null)
                {
                    s_playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                }
                s_browser = await s_playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    // 容器 / 小内存 Linux 机器上 /dev/shm 默认过小，会随机崩页
                    Args = new[] { "--disable-dev-shm-usage" },
                }).ConfigureAwait(false);
                return s_browser;
            }
            finally
            {
                BrowserLock.Release();
            }
        }

        private static async Task<bool> WaitForListAsync(IPage page)
        {
            try
            {
                await page.WaitForSelectorAsync(ListItemSelector, new PageWaitForSelectorOptions
                {
                    Timeout = ListTimeoutMs,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"自动截图等待文件列表失败：{ex.Message}");
                return false;
            }
            // 列表首帧可能只有骨架屏，稍等布局稳定再判断类型与裁剪位置
            await Task.Delay(800).ConfigureAwait(false);
            return true;
        }

        /// <summary>首个条目是文件夹就点进去；点不动或不是文件夹都保持当前页面。</summary>
        private static async Task TryEnterFirstFolderAsync(IPage page)
        {
            string[] firstItem;
            try
            {
                firstItem = await page.EvaluateAsync<string[]>(@"() => {
                    const item = document.querySelector('.mini-file-list .file-item');
                    if (!item) { return null; }
                    const title = item.querySelector('.item-tit');
                    const sub = item.querySelector('.item-sub-tit');
                    return [
                        title ? title.innerText.trim() : '',
                        sub ? sub.innerText.trim() : ''
                    ];
                }").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"自动截图读取首项失败：{ex.Message}");
                return;
            }

            if (firstItem == null || firstItem.Length < 2 || string.IsNullOrWhiteSpace(firstItem[0]))
            {
                return;
            }

            // 文件夹的副标题是「项数」（如「2项」），文件是大小（如「1G」「29.9KB」）
            if (!Regex.IsMatch(firstItem[1] ?? string.Empty, @"\d+\s*项\s*$"))
            {
                return;
            }

            string previousName = firstItem[0];
            try
            {
                await page.Locator(ListItemSelector).First.Locator(".item-tit")
                    .ClickAsync(new LocatorClickOptions { Timeout = EnterFolderTimeoutMs })
                    .ConfigureAwait(false);

                await page.WaitForFunctionAsync(@"previous => {
                    const items = document.querySelectorAll('.mini-file-list .file-item');
                    if (!items.length) { return false; }
                    const title = items[0].querySelector('.item-tit');
                    const name = title ? title.innerText.trim() : '';
                    const header = document.querySelector('[class*=""content-header""]');
                    // 进入子目录后表头会出现「返回」，首项名称通常也会变化
                    return name !== previous || (header && header.innerText.indexOf('返回') >= 0);
                }", previousName, new PageWaitForFunctionOptions
                {
                    Timeout = EnterFolderTimeoutMs,
                    PollingInterval = 200,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                // 进入失败不阻断截图：退化为截当前（根目录）画面
                LogHelper.Info($"自动截图进入首个文件夹失败（{previousName}）：{ex.Message}");
            }

            // 等列表渲染稳定，避免截到过渡中的空白
            await Task.Delay(1000).ConfigureAwait(false);
        }

        /// <summary>
        /// 切换为缩略图（网格）视图：点击内容表头的视图模式切换按钮（span.ShareDetail--header-mode--*）。
        /// 点击前先确认页面里确有该按钮，点击失败时用 DOM click 兜底，点击后尝试检测列表是否出现缩略图。
        /// 任何异常都不阻断截图：按钮不存在 / 点击失败 / 切换失败一律按当前列表视图继续，
        /// 并把命中的按钮 class 写进日志便于排查选择器是否匹配。
        /// </summary>
        private static async Task TrySwitchToThumbnailViewAsync(IPage page)
        {
            try
            {
                // 1) 预检：页面里是否有视图切换按钮；把命中的 class 记入日志，方便人工核对选择器
                string[] candidates = Array.Empty<string>();
                try
                {
                    candidates = await page.EvaluateAsync<string[]>(@"() => Array.from(
                        document.querySelectorAll('span[class*=""ShareDetail--header-mode""]'))
                        .map(s => s.className || '')").ConfigureAwait(false) ?? Array.Empty<string>();
                }
                catch (Exception ex)
                {
                    LogHelper.Info($"自动截图切换缩略图视图预检失败（按列表视图截图）：{ex.Message}");
                    return;
                }
                if (candidates.Length == 0)
                {
                    LogHelper.Info("自动截图未找到视图切换按钮（span.ShareDetail--header-mode），按列表视图截图");
                    return;
                }
                LogHelper.Info($"自动截图命中视图切换候选 {candidates.Length} 个：{string.Join(", ", candidates)}");

                // 2) 逐个候选尝试点击，直到列表中出现缩略图特征或候选点完
                //    优先 Playwright 可操作性点击，失败（不可见 / 被遮挡 / 动画中）时用 DOM click 兜底
                for (int i = 0; i < candidates.Length && i < 3; i++)
                {
                    bool clicked;
                    try
                    {
                        await page.Locator(ViewModeSwitchSelector).Nth(i)
                            .ClickAsync(new LocatorClickOptions
                            {
                                Timeout = 4000,
                                NoWaitAfter = true,
                            }).ConfigureAwait(false);
                        clicked = true;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Info($"自动截图点击候选 #{i} 失败，改用 DOM click：{ex.Message}");
                        clicked = await page.EvaluateAsync<bool>(@"index => {
                            const el = document.querySelectorAll(
                                'span[class*=""ShareDetail--header-mode""]')[index];
                            if (!el) { return false; }
                            el.click();
                            return true;
                        }", i).ConfigureAwait(false);
                    }
                    if (!clicked)
                    {
                        continue;
                    }
                    LogHelper.Info($"自动截图已点击视图切换候选 #{i}");
                    await Task.Delay(800).ConfigureAwait(false);

                    // 3) 校验是否真的切到了缩略图视图：列表条目内出现 img 或缩略图容器
                    bool switched = false;
                    try
                    {
                        await page.WaitForFunctionAsync(@"() => {
                            const items = document.querySelectorAll('.mini-file-list .file-item');
                            if (items.length === 0) { return false; }
                            for (let i = 0; i < Math.min(items.length, 4); i++) {
                                const el = items[i];
                                if (el.querySelector('img')) { return true; }
                                if (el.querySelector('.thumbnail, [class*=""thumb""]')) { return true; }
                            }
                            return false;
                        }", new PageWaitForFunctionOptions
                        {
                            Timeout = 5000,
                            PollingInterval = 250,
                        }).ConfigureAwait(false);
                        switched = true;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Info($"自动截图校验候选 #{i} 未切换为缩略图：{ex.Message}");
                    }
                    if (switched)
                    {
                        LogHelper.Info("自动截图确认已切换缩略图视图");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Info($"自动截图切换缩略图视图异常（按列表视图截图）：{ex.Message}");
            }
            // 切换动画 / 图片加载留出稳定时间
            await Task.Delay(1000).ConfigureAwait(false);
        }

        /// <summary>
        /// 从内容表头开始向下截到文件列表前 8 个条目结束；不足 8 个按 8 个高度预留底部留白；
        /// 定位不到表头 / 列表 / 坐标异常时退化为「表头下 620px」的保守区域，避免整视口多截无关页面。
        /// </summary>
        private static async Task<byte[]> TakeScreenshotAsync(IPage page)
        {
            double headerTop = 0;
            double bottomTarget = -1;
            double viewportHeight = ViewportHeight;
            try
            {
                headerTop = await page.EvaluateAsync<double>(@"() => {
                    const header = document.querySelector('[class*=""content-header""]');
                    if (header) { return header.getBoundingClientRect().top; }
                    return 0;
                }").ConfigureAwait(false);

                // 列表几何：条目数、首末条目底部、按条目数外推/取第 8 条得到的建议下边界（视口坐标）。
                // JS 端直接算好，避免 Playwright 脚本和 C# 端两套计算不一致。
                string geometry = await page.EvaluateAsync<string>(@"() => {
                    const rect = (el) => el.getBoundingClientRect();
                    const header = document.querySelector('[class*=""content-header""]');
                    let list = header ? header.querySelector('.mini-file-list') : null;
                    if (!list) { list = document.querySelector('.mini-file-list'); }
                    const items = list ? Array.from(list.querySelectorAll('.file-item')) : [];
                    let bottom = -1;
                    if (items.length > 0) {
                        const firstTop = rect(items[0]).top;
                        if (items.length >= 8) {
                            bottom = rect(items[7]).bottom;
                        } else {
                            const lastBottom = rect(items[items.length - 1]).bottom;
                            const avgStep = (lastBottom - firstTop) / items.length;
                            bottom = lastBottom + (8 - items.length) * avgStep;
                        }
                    }
                    return JSON.stringify({
                        counted: items.length,
                        first: items.length > 0 ? rect(items[0]).top : -1,
                        last: items.length > 0 ? rect(items[items.length - 1]).bottom : -1,
                        bottom: bottom
                    });
                }").ConfigureAwait(false);
                LogHelper.Info($"自动截图列表几何：{geometry}");
                using (JsonDocument doc = JsonDocument.Parse(geometry))
                {
                    JsonElement root = doc.RootElement;
                    bottomTarget = root.GetProperty("bottom").GetDouble();
                }
                viewportHeight = await page.EvaluateAsync<double>("() => window.innerHeight").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"自动截图定位内容表头 / 文件列表失败：{ex.Message}");
            }

            if (viewportHeight <= 0)
            {
                viewportHeight = ViewportHeight;
            }
            // 表头位置异常（未定位到 / 超出视口）时回退到视口顶部，保证有可用图片
            if (headerTop < 0 || headerTop >= viewportHeight - 40)
            {
                headerTop = 0;
            }
            // 下边界合并：第 8 个 item 底部在表头之下时取它（超出视口则截到视口底部）；
            // 定位失败或坐标异常时退化为「表头下 620px」的保守区域，避免整视口把列表下方的无关页面一起截进来
            double clipBottom;
            if (bottomTarget > headerTop && bottomTarget < viewportHeight + 40)
            {
                clipBottom = Math.Min(bottomTarget, viewportHeight);
            }
            else
            {
                clipBottom = Math.Min(headerTop + FallbackClipHeight, viewportHeight);
            }
            double clipHeight = clipBottom - headerTop;
            // 高度异常（如条目紧贴表头）时退化为整视口，避免向 Playwright 传入非正高度
            if (clipHeight <= 0)
            {
                headerTop = 0;
                clipHeight = viewportHeight;
            }

            LogHelper.Info($"自动截图裁剪区：headerTop={headerTop:F0} target={bottomTarget:F0} bottom={clipBottom:F0} height={clipHeight:F0}");

            // 缩略图视图下画面像素大（DPR3），合成 PNG 可能较慢，显式放宽截图超时避免吃掉默认超时上限
            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Timeout = 45000,
                Clip = new Clip
                {
                    X = 0,
                    Y = (float)headerTop,
                    Width = ViewportWidth,
                    Height = (float)clipHeight,
                },
            }).ConfigureAwait(false);
        }

        /// <summary>列表没渲染出来时判断原因（提取码 / 失效 / 超时）。</summary>
        private static async Task<string> DetectBlockReasonAsync(IPage page)
        {
            string text;
            try
            {
                text = await page.EvaluateAsync<string>(@"() => {
                    const hasPassword = !!document.querySelector('input[type=""password""]') ||
                        !!document.querySelector('input[placeholder*=""提取码""]') ||
                        !!document.querySelector('input[placeholder*=""密码""]');
                    const body = document.body ? document.body.innerText.replace(/\s+/g, ' ') : '';
                    return (hasPassword ? '[PWD]' : '') + body.slice(0, 600);
                }").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"自动截图读取页面内容失败：{ex.Message}");
                return ErrorTimeout;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return ErrorTimeout;
            }
            if (text.StartsWith("[PWD]", StringComparison.Ordinal) || text.Contains("输入提取码"))
            {
                return ErrorPassword;
            }
            if (text.Contains("失效") || text.Contains("不存在") || text.Contains("来晚了") ||
                text.Contains("已取消") || text.Contains("违规"))
            {
                return ErrorShareGone;
            }
            return ErrorTimeout;
        }

        /// <summary>只接受夸克分享页链接，避免后台浏览器被指向任意地址。</summary>
        private static bool TryNormalizeUrl(string rawUrl, string code, out string targetUrl, out string error)
        {
            targetUrl = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                error = ErrorInvalidUrl;
                return false;
            }

            string url = rawUrl.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                error = ErrorInvalidUrl;
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = ErrorInvalidUrl;
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            if (host != "pan.quark.cn" && !host.EndsWith(".pan.quark.cn", StringComparison.Ordinal))
            {
                error = ErrorNotQuark;
                return false;
            }
            if (!uri.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            {
                error = ErrorInvalidUrl;
                return false;
            }

            // 链接本身带 pwd 时以链接为准，否则用记录的提取码补上
            if (!string.IsNullOrWhiteSpace(code) &&
                url.IndexOf("pwd=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                url += (url.Contains('?') ? "&" : "?") + "pwd=" + Uri.EscapeDataString(code.Trim());
            }

            targetUrl = url;
            return true;
        }

        /// <summary>把 Playwright 异常翻译成可展示的中文原因，避免把堆栈抛给前端。</summary>
        private static string DescribeException(Exception ex)
        {
            string message = ex == null ? string.Empty : (ex.Message ?? string.Empty);
            if (message.IndexOf("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("playwright install", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("BrowserType.launch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ErrorBrowserMissing;
            }

            string firstLine = message.Split('\n')[0].Trim();
            if (firstLine.Length > 120)
            {
                firstLine = firstLine.Substring(0, 120);
            }
            if (firstLine.Length == 0)
            {
                return "截图失败";
            }
            if (firstLine.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ErrorTimeout;
            }
            return "截图失败：" + firstLine;
        }
    }
}
