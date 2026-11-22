using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：夸克分享页移动端截图器（Playwright 无头 Chromium）。
    /// 以手机视口（360x732、DPR 3、Android UA、触摸）打开分享链接：首个条目是文件夹则点进去，
    /// 首个条目是文件 / 压缩包则停在根目录；从内容表头（.ShareDetail--content-header）向下截到视口底部，
    /// 不包含顶部「打开 APP / 分享人 / 文件总数 / 容量 / 举报」区域。
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

        /// <summary>移动端视口宽高（CSS 像素），与前台 H5 页保持一致。</summary>
        private const int ViewportWidth = 360;
        private const int ViewportHeight = 732;

        /// <summary>页面跳转超时：夸克 H5 为 SPA，DOMContentLoaded 之后还需等待列表渲染。</summary>
        private const int NavigationTimeoutMs = 25000;

        /// <summary>等待文件列表渲染的超时。</summary>
        private const int ListTimeoutMs = 12000;

        /// <summary>点击条目进入文件夹后，等待列表切换的超时。</summary>
        private const int EnterFolderTimeoutMs = 10000;

        /// <summary>条目列表选择器：夸克的 class 带构建哈希，这里只用稳定的前缀类名。</summary>
        private const string ListItemSelector = ".mini-file-list .file-item";

        /// <summary>Android Chrome 移动端 UA，让分享页走 H5 版（路由 #/list/share）。</summary>
        private const string MobileUserAgent =
            "Mozilla/5.0 (Linux; Android 16; Pixel 10) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/149.0.0.0 Mobile Safari/537.36";

        private static readonly SemaphoreSlim BrowserLock = new SemaphoreSlim(1, 1);
        private static IPlaywright s_playwright;
        private static IBrowser s_browser;

        /// <summary>
        /// 截图并返回 PNG 字节与失败原因（成功时 Error 为空）。
        /// 失败原因均为可直接展示给用户的中文短句。
        /// </summary>
        public static async Task<(byte[] Bytes, string Error)> CaptureAsync(string shareUrl, string code, CancellationToken token)
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

                byte[] bytes = await TakeScreenshotAsync(page).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                {
                    return (null, "截图内容为空");
                }
                return (bytes, string.Empty);
            }
            catch (PlaywrightException ex)
            {
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
            catch (PlaywrightException)
            {
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
            catch (PlaywrightException ex)
            {
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
            catch (PlaywrightException ex)
            {
                // 进入失败不阻断截图：退化为截当前（根目录）画面
                LogHelper.Info($"自动截图进入首个文件夹失败（{previousName}）：{ex.Message}");
            }

            // 等列表渲染稳定，避免截到过渡中的空白
            await Task.Delay(1000).ConfigureAwait(false);
        }

        /// <summary>从内容表头开始向下截到视口底部；定位不到表头时退化为整个视口。</summary>
        private static async Task<byte[]> TakeScreenshotAsync(IPage page)
        {
            double headerTop = 0;
            double viewportHeight = ViewportHeight;
            try
            {
                headerTop = await page.EvaluateAsync<double>(@"() => {
                    const header = document.querySelector('[class*=""content-header""]');
                    if (header) { return header.getBoundingClientRect().top; }
                    return 0;
                }").ConfigureAwait(false);
                viewportHeight = await page.EvaluateAsync<double>("() => window.innerHeight").ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                LogHelper.Info($"自动截图定位内容表头失败：{ex.Message}");
            }

            if (viewportHeight <= 0)
            {
                viewportHeight = ViewportHeight;
            }
            // 表头位置异常（未定位到 / 超出视口）时整视口截图，保证有可用图片
            if (headerTop < 0 || headerTop >= viewportHeight - 40)
            {
                headerTop = 0;
            }

            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Clip = new Clip
                {
                    X = 0,
                    Y = (float)headerTop,
                    Width = ViewportWidth,
                    Height = (float)(viewportHeight - headerTop),
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
            catch (PlaywrightException)
            {
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
