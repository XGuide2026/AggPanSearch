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
    /// 描 述：网盘分享页「资源信息分析」结果（三字段）。
    /// 对应数据库 pan_source_collect / pan_source 三个新列：
    ///   FileCount            文件个数   int?          提取自「共 N 个文件」
    ///   FileSize             文件大小   int?          提取自统计行内的带单位数字（如 6.2G），
    ///                                                  按 1024 进制换算为 M（6.2G -> 6349M）后入库
    ///   ShareValidityPeriod  资源有效期 string?       提取自统计区第二个 span（如 永久有效）
    /// </summary>
    public sealed class PanSourceInfoStat
    {
        public int? FileCount { get; set; }
        public int? FileSize { get; set; }
        public string ShareValidityPeriod { get; set; } = string.Empty;

        /// <summary>是否解析出至少一个有效字段。</summary>
        public bool HasAny
        {
            get
            {
                return FileCount != null && FileCount.Value > 0
                    || FileSize != null && FileSize.Value > 0
                    || ShareValidityPeriod.Length > 0;
            }
        }
    }

    /// <summary>
    /// 描 述：夸克分享页统计信息分析器（Playwright 无头 Chromium，桌面视口）。
    /// 打开夸克分享链接，等待页面统计区 .info-stat 渲染，读取其子 span 文本并按纯文本
    /// 规则解析出「文件个数 / 文件大小 / 资源有效期」三字段。页面残废原因（提取码 / 失效 /
    /// 超时 / 缺浏览器）返回可直接展示的中文文案，与截图器约定的错误文案保持一致。
    /// 浏览器进程在进程内复用，任务内串行调用；不记录含提取码的完整 URL。
    /// 页面结构示例（用户提供）：
    ///   <div class="info-stat"><span>共 112 个文件 6.2G</span><span>永久有效</span><span class="report-btn">举报</span></div>
    /// </summary>
    public static class QuarkShareInfoAnalyzer
    {
        public const string ErrorNotQuark = "仅支持夸克资源";
        public const string ErrorInvalidUrl = "资源链接不正确";
        public const string ErrorTimeout = "页面加载超时";
        public const string ErrorPassword = "需要提取码";
        public const string ErrorShareGone = "分享链接已失效";
        public const string ErrorBrowserMissing = "服务器未安装 Chromium（请执行 playwright install chromium）";
        public const string ErrorNoStat = "未读取到文件统计信息";

        /// <summary>桌面视口宽高（CSS 像素），与 PC 版分享页 DOM 一致。</summary>
        private const int ViewportWidth = 1280;
        private const int ViewportHeight = 720;

        /// <summary>页面跳转超时。</summary>
        private const int NavigationTimeoutMs = 25000;

        /// <summary>等待统计区 .info-stat 渲染的超时。</summary>
        private const int InfoStatTimeoutMs = 12000;

        /// <summary>资源有效期原文最大长度（varchar(100)）。</summary>
        private const int MaxValidityLength = 100;

        /// <summary>文件大小单位换算（1024 进制，存储单位 M）。</summary>
        private const int BytePerMB = 1024 * 1024;

        private static readonly SemaphoreSlim BrowserLock = new SemaphoreSlim(1, 1);
        private static IPlaywright s_playwright;
        private static IBrowser s_browser;

        /// <summary>
        /// 分析网盘分享页并返回三字段（成功时 Error 为空）。
        /// 失败原因均为可直接展示给用户的中文短句。
        /// </summary>
        public static async Task<(PanSourceInfoStat Stat, string Error)> AnalyzeAsync(
            string shareUrl, string code, CancellationToken token)
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
                    Locale = "zh-CN",
                }).ConfigureAwait(false);

                page = await context.NewPageAsync().ConfigureAwait(false);
                page.SetDefaultTimeout(InfoStatTimeoutMs);

                await page.GotoAsync(targetUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = NavigationTimeoutMs,
                }).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                // 等待 SPA 渲染出统计区 .info-stat；渲染不出来多半是提取码 / 失效 / 网络问题
                bool ready = await WaitForInfoStatAsync(page).ConfigureAwait(false);
                if (!ready)
                {
                    return (null, await DetectBlockReasonAsync(page).ConfigureAwait(false));
                }

                token.ThrowIfCancellationRequested();

                PanSourceInfoStat stat = await ReadInfoStatAsync(page).ConfigureAwait(false);
                if (!stat.HasAny)
                {
                    return (null, ErrorNoStat);
                }
                return (stat, string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 超时可能以不同异常类型抛出，统一转成可展示的中文原因
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

        /// <summary>等待统计区 .info-stat 出现。</summary>
        private static async Task<bool> WaitForInfoStatAsync(IPage page)
        {
            try
            {
                await page.WaitForSelectorAsync(".info-stat", new PageWaitForSelectorOptions
                {
                    Timeout = InfoStatTimeoutMs,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"资源信息分析等待 .info-stat 失败：{ex.Message}");
                return false;
            }
            // 统计区渲染后稍等布局稳定，避免读到骨架屏残留
            await Task.Delay(600).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// 读取统计区文本：逐个 .info-stat 节点收集，节点下有子 span 则取子 span，
        /// 否则取节点自身文本；每个节点记下 class 前缀，用于排除 report-btn（举报）等非统计项。
        /// </summary>
        private static async Task<PanSourceInfoStat> ReadInfoStatAsync(IPage page)
        {
            string json;
            try
            {
                json = await page.EvaluateAsync<string>(@"() => {
                    const rows = [];
                    const containers = document.querySelectorAll('.info-stat');
                    for (const c of containers) {
                        const spans = c.querySelectorAll(':scope > span');
                        if (spans.length > 0) {
                            for (const s of spans) {
                                rows.push([
                                    (s.className || '').toLowerCase(),
                                    (s.textContent || '').trim().replace(/\s+/g, ' ')
                                ]);
                            }
                        } else {
                            rows.push([
                                (c.className || '').toLowerCase(),
                                (c.textContent || '').trim().replace(/\s+/g, ' ')
                            ]);
                        }
                    }
                    return JSON.stringify(rows);
                }").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogHelper.Info($"资源信息分析读取统计区失败：{ex.Message}");
                return new PanSourceInfoStat();
            }

            return ParseStatRows(json);
        }

        /// <summary>
        /// 解析统计区文本行（纯文本规则，与具体 DOM class 解耦）：
        ///   1) 跳过 report-btn / 文本含「举报」的行；
        ///   2) 第一个有效行：匹配「(共 )N 个文件」，提取 FileCount，再从该行剩余文本提取带单位的
        ///      文件大小并按 1024 进制换算为 M（如 6.2G -> 6349M）；若该行没有「N 个文件」模式，
        ///      则整行尝试按文件大小解析；
        ///   3) 剩余行中第一个非统计文本作为 ShareValidityPeriod 原文（如「永久有效」）。
        /// </summary>
        private static PanSourceInfoStat ParseStatRows(string json)
        {
            PanSourceInfoStat stat = new PanSourceInfoStat();
            if (string.IsNullOrWhiteSpace(json))
            {
                return stat;
            }

            JsonElement root;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    root = doc.RootElement.Clone();
                }
            }
            catch (Exception ex)
            {
                LogHelper.Info($"资源信息分析解析统计区 JSON 失败：{ex.Message}");
                return stat;
            }

            bool firstSeen = false;
            foreach (JsonElement row in root.EnumerateArray())
            {
                string cls = row.GetArrayLength() > 0 ? row[0].GetString() ?? string.Empty : string.Empty;
                string text = row.GetArrayLength() > 1 ? row[1].GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(text) ||
                    cls.IndexOf("report", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("举报", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                if (!firstSeen)
                {
                    firstSeen = true;
                    Match countMatch = Regex.Match(text, @"(?:共\s*)?(\d+)\s*个文件");
                    if (countMatch.Success)
                    {
                        if (int.TryParse(countMatch.Groups[1].Value, out int count) && count > 0)
                        {
                            stat.FileCount = count;
                        }
                        // 大小在「N 个文件」之后的剩余文本里（如「6.2G」）
                        int remainIndex = countMatch.Index + countMatch.Length;
                        string remainder = text.Substring(remainIndex).Trim()
                            .TrimStart(':').Trim().TrimStart('：').Trim();
                        int? size1 = ExtractFileSizeToMB(remainder);
                        if (size1 != null)
                        {
                            stat.FileSize = size1.Value;
                        }
                    }
                    else
                    {
                        int? size2 = ExtractFileSizeToMB(text);
                        if (size2 != null)
                        {
                            stat.FileSize = size2.Value;
                        }
                        else
                        {
                            stat.ShareValidityPeriod = Truncate(text, MaxValidityLength);
                        }
                    }
                }
                else if (stat.ShareValidityPeriod.Length == 0)
                {
                    stat.ShareValidityPeriod = Truncate(text, MaxValidityLength);
                }
            }

            return stat;
        }

        /// <summary>
        /// 从文本中提取带单位的文件大小（如 6.2G / 1.2GB / 820K / 512B），按 1024 进制换算为 M
        /// 四舍五入取整后返回；提取不到或结果不足 1M 返回 null。
        /// 单位换算参考：1T=1024G，1G=1024M，1M=1024K(KB)，1K(KB)=1024B。
        /// </summary>
        private static int? ExtractFileSizeToMB(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            Match match = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*([KMGTP]?B?)\b", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }
            if (!double.TryParse(match.Groups[1].Value.Trim(), out double num))
            {
                return null;
            }
            string unit = match.Groups[2].Value.ToUpperInvariant();

            double mb;
            switch (unit)
            {
                case "T":
                case "TB":
                    mb = num * 1024 * 1024; // T -> M
                    break;
                case "G":
                case "GB":
                    mb = num * 1024;         // G -> M
                    break;
                case "M":
                case "MB":
                    mb = num;                // 已是 M
                    break;
                case "K":
                case "KB":
                    mb = num / 1024;         // K -> M
                    break;
                case "B":
                    mb = num / BytePerMB;    // B -> M
                    break;
                default:
                    return null;
            }

            if (!(mb >= 1))
            {
                return null; // 不足 1M（如 512B / 0.8K）不入库，避免 0 或负值
            }
            return (int)Math.Round(mb, MidpointRounding.AwayFromZero);
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

        /// <summary>统计区没渲染出来时判断原因（提取码 / 失效 / 超时）。</summary>
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
                LogHelper.Info($"资源信息分析读取页面内容失败：{ex.Message}");
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
                return "资源信息分析失败";
            }
            if (firstLine.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ErrorTimeout;
            }
            return "资源信息分析失败：" + firstLine;
        }

        /// <summary>按字符数截断，超长截断并清理首尾空白。</summary>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            string text = value.Trim();
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}