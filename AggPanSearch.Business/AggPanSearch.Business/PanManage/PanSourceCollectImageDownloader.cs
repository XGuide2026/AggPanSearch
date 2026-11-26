using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集资源图片下载器。
    /// 把 collect_image_url 中的远端图片逐张下载到本机 Resource/Collect/年/月/日/ 下，
    /// 返回「仍然可用的原图地址」与「对应的本地地址」，两者用分号拼接且同索引一一对应；
    /// 下载失败（非成功响应、超时、非图片、超过大小上限）的地址会被剔除，不进入返回结果。
    /// </summary>
    public static class PanSourceCollectImageDownloader
    {
        /// <summary>单张图片大小上限（5MB）。</summary>
        private const int MaxBytes = 5 * 1024 * 1024;

        /// <summary>单张图片下载超时时间。</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

        /// <summary>下载完每张图片后的暂停间隔（毫秒）。无论成功下载还是失败剔除，每处理一张都暂停一次，限速节奏更稳定。</summary>
        private const int DelayAfterImageMilliseconds = 500;

        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

        private static readonly Dictionary<string, string> MediaTypeExtensions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["image/jpeg"] = ".jpg",
                ["image/jpg"] = ".jpg",
                ["image/png"] = ".png",
                ["image/gif"] = ".gif",
                ["image/webp"] = ".webp",
                ["image/bmp"] = ".bmp",
                ["image/x-ms-bmp"] = ".bmp",
            };

        private static readonly HttpClient Client = CreateClient();

        /// <summary>
        /// 逐张下载多图并返回两个分号拼接的字符串（同索引一一对应）。
        /// 全部下载失败时两个返回值都是空串。
        /// </summary>
        /// <param name="collectImageUrl">原图片地址（分号分隔的多图串）</param>
        /// <param name="sourceUrl">采集来源页地址，用于生成 Referer 绕过防盗链</param>
        /// <param name="token">取消令牌</param>
        public static async Task<(string CollectImageUrl, string ImageUrl)> DownloadAsync(
            string? collectImageUrl, string? sourceUrl, CancellationToken token)
        {
            List<string> remoteImages = SiteCollectHtmlUtil.SplitImages(collectImageUrl)
                .Where(image => SiteCollectHtmlUtil.TryCreateHttpUri(image, out _))
                .Take(SiteCollectHtmlUtil.MaxImageCount)
                .ToList();
            if (remoteImages.Count == 0)
            {
                return (string.Empty, string.Empty);
            }

            string referer = ResolveReferer(sourceUrl);
            List<string> keptImages = new List<string>();
            List<string> localImages = new List<string>();
            foreach (string remoteImage in remoteImages)
            {
                token.ThrowIfCancellationRequested();
                string localImage = await DownloadOneAsync(remoteImage, referer, token).ConfigureAwait(false);
                if (localImage.Length != 0)
                {
                    // 失效图直接剔除，不保留在返回结果里
                    keptImages.Add(remoteImage);
                    localImages.Add(localImage);
                }
                // 下载限速：每张图片处理完（含失败剔除）都暂停一次，防止请求过频触发站点风控
                await Task.Delay(DelayAfterImageMilliseconds, token).ConfigureAwait(false);
            }
            return (string.Join(";", keptImages), string.Join(";", localImages));
        }

        /// <summary>判断本地图片地址对应的文件是否仍然存在（补图任务幂等判断用）。</summary>
        public static bool LocalFileExists(string? localImageUrl)
        {
            if (string.IsNullOrWhiteSpace(localImageUrl))
            {
                return false;
            }
            string relativePath = localImageUrl.Trim().TrimStart('/', '\\')
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relativePath.Length == 0)
            {
                return false;
            }
            string absolutePath = Path.Combine(GlobalContext.HostingEnvironment.ContentRootPath, relativePath);
            return File.Exists(absolutePath);
        }

        /// <summary>下载单张图片并落盘，返回以 / 开头的本地地址；失败返回空串。</summary>
        private static async Task<string> DownloadOneAsync(string url, string referer, CancellationToken token)
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(RequestTimeout);

                HttpResponseMessage response = await Client
                    .SendAsync(BuildImageRequest(url, referer), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden &&
                        await TryPassShieldAsync(url, referer, token).ConfigureAwait(false))
                    {
                        // 站点安全盾校验通过后 Cookie 已写入共享容器，重试一次
                        response.Dispose();
                        response = await Client
                            .SendAsync(BuildImageRequest(url, referer), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                            .ConfigureAwait(false);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        LogHelper.Info($"采集图片下载失败：{url}，状态码 {(int)response.StatusCode}");
                        return string.Empty;
                    }

                    string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        LogHelper.Info($"采集图片下载失败：{url}，响应类型 {mediaType}");
                        return string.Empty;
                    }
                    if (response.Content.Headers.ContentLength > MaxBytes)
                    {
                        LogHelper.Info($"采集图片下载失败：{url}，超过 {MaxBytes / 1024 / 1024}MB");
                        return string.Empty;
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    if (bytes.Length == 0 || bytes.Length > MaxBytes)
                    {
                        return string.Empty;
                    }

                    string extension = ResolveExtension(url, mediaType);
                    if (extension.Length == 0)
                    {
                        LogHelper.Info($"采集图片下载失败：{url}，不支持的图片类型 {mediaType}");
                        return string.Empty;
                    }

                    string relativeDirectory = BuildRelativeDirectory();
                    string absoluteDirectory = Path.Combine(GlobalContext.HostingEnvironment.ContentRootPath, relativeDirectory);
                    if (!Directory.Exists(absoluteDirectory))
                    {
                        Directory.CreateDirectory(absoluteDirectory);
                    }
                    string fileName = SecurityHelper.GetGuid(true) + extension;
                    await File.WriteAllBytesAsync(Path.Combine(absoluteDirectory, fileName), bytes, cts.Token)
                        .ConfigureAwait(false);
                    return "/" + FileHelper.ConvertDirectoryToHttp(Path.Combine(relativeDirectory, fileName));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                LogHelper.Info($"采集图片下载超时：{url}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogHelper.Info($"采集图片下载异常：{url}，{ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>构造图片下载请求：浏览器 UA、图片 Accept、防盗链 Referer。</summary>
        private static HttpRequestMessage BuildImageRequest(string url, string referer)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/png,image/*,*/*;q=0.8");
            if (referer.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }
            return request;
        }

        /// <summary>
        /// 图片被站点安全盾（WTS）拦截时的兜底：借用页面抓取器完成校验，
        /// 令牌写入两者共享的 Cookie 容器，返回是否已通过。
        /// </summary>
        private static async Task<bool> TryPassShieldAsync(string url, string referer, CancellationToken token)
        {
            if (!HtmlPageFetcher.IsAllowedUrl(url, out _))
            {
                return false;
            }
            PageFetchResult result = await HtmlPageFetcher.FetchAsync(url, referer, null, token).ConfigureAwait(false);
            return result.Success;
        }

        /// <summary>本地目录：Resource/Collect/年/月/日（自动截图等本地图片共用同一套目录规则）。</summary>
        public static string BuildRelativeDirectory()
        {
            DateTime now = DateTime.Now;
            return Path.Combine(
                "Resource",
                "Collect",
                now.ToString("yyyy"),
                now.ToString("MM"),
                now.ToString("dd"));
        }

        /// <summary>Referer 取采集来源页的站点根，避免外链图床校验完整路径。</summary>
        private static string ResolveReferer(string? sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl) ||
                !SiteCollectHtmlUtil.TryCreateHttpUri(sourceUrl, out Uri uri))
            {
                return string.Empty;
            }
            return uri.GetLeftPart(UriPartial.Authority) + "/";
        }

        /// <summary>优先按响应的 Content-Type 定扩展名，其次按地址扩展名；不在白名单内返回空串。</summary>
        private static string ResolveExtension(string url, string mediaType)
        {
            if (MediaTypeExtensions.TryGetValue(mediaType, out string? extension))
            {
                return extension;
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                string urlExtension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (AllowedExtensions.Contains(urlExtension))
                {
                    return urlExtension;
                }
            }
            return string.Empty;
        }

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                // 复用采集会话 Cookie：站点安全盾（WTS）校验通过后图片请求才不会被拦
                UseCookies = true,
                CookieContainer = HtmlPageFetcher.SessionCookies,
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }
}
