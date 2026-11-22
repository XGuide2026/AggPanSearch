using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.Detection;
using AggPanSearch.Models;
using Newtonsoft.Json.Linq;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Services
{
    /// <summary>前端批量传入的一条待检测链接（兼容字符串与 {url,password} 对象两种入参形态）。</summary>
    public sealed class PanCheckLink
    {
        public string Url { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>单条链接检测结果。Valid 三态：true=有效(含需提取码)、false=失效、null=不确定/暂不支持。</summary>
    public sealed class PanLinkCheckResult
    {
        public string Url { get; set; } = "";
        public bool? Valid { get; set; }
        public string Message { get; set; } = "";
        public string Type { get; set; } = "";
    }

    /// <summary>
    /// 网盘分享链接有效性检测服务（移植自 fish2018/pansou 的 service/check_service.go）。
    /// 按网盘类型逐平台探测（夸克/百度/阿里/UC/天翼/123/迅雷/115），
    /// 有效标准：能从官方公开接口或页面拿到具体资源元数据（分享名非空 / 文件数 &gt; 0 / 文件列表非空）。
    /// 注册为单例，内置进程内 TTL 缓存、在途请求合并（inflight dedup）与信号量并发限流。
    /// </summary>
    public sealed class PanLinkCheckService
    {
        private enum State
        {
            Ok,
            Bad,
            Locked,
            Unsupported,
            Uncertain,
        }

        private sealed class CacheEntry
        {
            public required State State { get; init; }
            public required string Summary { get; init; }
            public required DateTime ExpiresAt { get; init; }
        }

        private const int MaxConcurrency = 4;

        private static readonly string MobileUa =
            "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
        private static readonly string Chrome124Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        // ---- 分享码提取正则（静态编译，仅编译一次） ----
        private static readonly Regex QuarkShareRegex = new Regex(@"/s/([A-Za-z0-9]+)", RegexOptions.Compiled);
        private static readonly Regex XunleiShareRegex = new Regex(@"pan\.xunlei\.com/s/([^?/#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BaiduSurlParamRegex = new Regex(@"[?&]surl=([a-zA-Z0-9]+)", RegexOptions.Compiled);
        private static readonly Regex TianyiShareIdRegex = new Regex(@"<shareId>\s*(\d+)\s*</shareId>", RegexOptions.Compiled);
        private static readonly Regex TianyiNeedCodeRegex = new Regex(@"<needAccessCode>\s*(\d+)\s*</needAccessCode>", RegexOptions.Compiled);
        private static readonly Regex TianyiFileNameRegex = new Regex(@"<fileName>\s*([^<]+?)\s*</fileName>", RegexOptions.Compiled);

        // ---- 迅雷 captcha 签名参数（与 pansou 保持一致） ----
        private const string XunleiClientId = "ZUBzD9J_XPXfn7f7";
        private const string XunleiDeviceId = "5505bd0cab8c9469b98e5891d9fb3e0d";
        private const string XunleiClientVersion = "1.10.0.2633";
        private const string XunleiPackageName = "com.xunlei.browser";
        private static readonly string[] XunleiSignatureSeeds =
        {
            "uWRwO7gPfdPB/0NfPtfQO+71",
            "F93x+qPluYy6jdgNpq+lwdH1ap6WOM+nfz8/V",
            "0HbpxvpXFsBK5CoTKam",
            "dQhzbhzFRcawnsZqRETT9AuPAJ+wTQso82mRv",
            "SAH98AmLZLRa6DB2u68sGhyiDh15guJpXhBzI",
            "unqfo7Z64Rie9RNHMOB",
            "7yxUdFADp3DOBvXdz0DPuKNVT35wqa5z0DEyEvf",
            "RBG",
            "ThTWPG5eC0UBqlbQ+04nZAptqGCdpv9o55A",
        };

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task<PanLinkCheckResult>> _inflight = new(StringComparer.Ordinal);

        public PanLinkCheckService()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = 16,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            _http = new HttpClient(handler)
            {
                // 全局单请求超时 15s，覆盖各平台 8~12s 的探测预算，失败不进缓存
                Timeout = TimeSpan.FromSeconds(15),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(Chrome124Ua);
        }

        /// <summary>
        /// 批量并发检测。同批内相同链接由 TTL 缓存与在途合并去重，对外只放行 MaxConcurrency 个并发探测。
        /// </summary>
        /// <returns>逐条结果（顺序与入参一致），单条失败不影响其他链接。</returns>
        public async Task<List<PanLinkCheckResult>> CheckAsync(IReadOnlyList<PanCheckLink> links, CancellationToken ct = default)
        {
            if (links == null || links.Count == 0)
            {
                return new List<PanLinkCheckResult>();
            }

            using var gate = new SemaphoreSlim(MaxConcurrency);
            var tasks = new List<Task<PanLinkCheckResult>>(links.Count);
            foreach (var link in links)
            {
                tasks.Add(CheckOneGuardedAsync(link, gate, ct));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToList();
        }

        private async Task<PanLinkCheckResult> CheckOneGuardedAsync(PanCheckLink link, SemaphoreSlim gate, CancellationToken ct)
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Build(link.Url, "", State.Uncertain, "检测已取消");
            }

            try
            {
                return await CheckOneAsync(link, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<PanLinkCheckResult> CheckOneAsync(PanCheckLink link, CancellationToken ct)
        {
            var original = string.IsNullOrWhiteSpace(link.Url) ? "" : link.Url.Trim();
            if (original.Length == 0)
            {
                return Build(original, "", State.Uncertain, "链接为空");
            }

            var type = NetDiskDetector.GetLinkType(original);
            if (type == CloudTypes.Others)
            {
                return Build(original, "", State.Unsupported, "当前平台暂不支持检测");
            }
            if (type == CloudTypes.Mobile)
            {
                // 移动云盘需 AES 加密协议（caiyun.139.com），本期暂无密钥算法实现，留作扩展位
                return Build(original, type, State.Unsupported, "移动云盘暂不支持检测");
            }

            var normalized = NormalizeShareLink(original, link.Password, type);
            var password = ResolvePassword(original, link.Password, type);
            var key = BuildCacheKey(type, normalized, password);

            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return Build(original, type, cached.State, cached.Summary);
            }
            _cache.TryRemove(key, out _);

            // 在途合并：同一 key 只发起一次真实探测，其他请求等待结果
            var task = _inflight.GetOrAdd(key, _ => RunCheckGuardedAsync(link, original, type, normalized, password, key, ct));
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        private async Task<PanLinkCheckResult> RunCheckGuardedAsync(PanCheckLink link, string original, string type,
            string normalized, string password, string cacheKey, CancellationToken ct)
        {
            try
            {
                var (state, summary) = await RunCheckAsync(original, type, normalized, password, ct).ConfigureAwait(false);
                _cache[cacheKey] = new CacheEntry
                {
                    State = state,
                    Summary = summary,
                    ExpiresAt = DateTime.UtcNow.Add(TtlFor(state)),
                };
                return Build(original, type, state, summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Build(original, type, State.Uncertain, "检测已取消");
            }
            catch (Exception ex)
            {
                return Build(original, type, State.Uncertain, "检测失败：" + ex.Message);
            }
        }

        private async Task<(State State, string Summary)> RunCheckAsync(string original, string type,
            string normalized, string password, CancellationToken ct)
        {
            switch (type)
            {
                case CloudTypes.Quark:
                    return await CheckQuarkAsync(normalized, password, ct).ConfigureAwait(false);
                case CloudTypes.Baidu:
                    return await CheckBaiduAsync(normalized, password, ct).ConfigureAwait(false);
                case CloudTypes.Aliyun:
                    return await CheckAliyunAsync(normalized, password, ct).ConfigureAwait(false);
                case CloudTypes.Uc:
                    return await CheckUcAsync(normalized, ct).ConfigureAwait(false);
                case CloudTypes.Tianyi:
                    return await CheckTianyiAsync(normalized, password, ct).ConfigureAwait(false);
                case CloudTypes.Pan123:
                    return await CheckPan123Async(normalized, ct).ConfigureAwait(false);
                case CloudTypes.Xunlei:
                    return await CheckXunleiAsync(normalized, password, ct).ConfigureAwait(false);
                case CloudTypes.Pan115:
                    return await CheckPan115Async(normalized, password, ct).ConfigureAwait(false);
                default:
                    return (State.Unsupported, "当前平台暂不支持检测");
            }
        }

        // =====================================================================
        //    探测方法：夸克 / 百度 / 阿里云盘
        // =====================================================================

        // ---- 夸克网盘：token 接口取 stoken → detail 接口看 share 状态 ----
        private async Task<(State, string)> CheckQuarkAsync(string normalized, string password, CancellationToken ct)
        {
            var m = QuarkShareRegex.Match(normalized);
            if (!m.Success)
            {
                return (State.Uncertain, "无法解析分享地址");
            }
            var pwdId = m.Groups[1].Value;

            // Step1: 获取访问令牌（code=0 表示分享链接有效；41008=需提取码）
            var tokenPayload = new JObject
            {
                ["pwd_id"] = pwdId,
                ["passcode"] = password,
                ["support_visit_limit_private_share"] = true,
            };
            var headers = new Dictionary<string, string>
            {
                ["origin"] = "https://pan.quark.cn",
                ["referer"] = "https://pan.quark.cn/",
            };
            var tokenResp = await FetchAsync(HttpMethod.Post,
                "https://drive-h.quark.cn/1/clouddrive/share/sharepage/token", tokenPayload.ToString(),
                null, headers, ct).ConfigureAwait(false);
            if (tokenResp.StatusCode == 0)
            {
                return (State.Uncertain, tokenResp.Error);
            }
            if (tokenResp.Content.Length == 0)
            {
                return (State.Uncertain, "请求失败");
            }

            JObject token;
            try
            {
                token = JObject.Parse(tokenResp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            int code = ValueInt(token["code"]);
            switch (code)
            {
                case 0:
                    break;
                case 41008:
                    return (State.Locked, "需要提取码");
                case 41004:
                case 41010:
                case 41011:
                    return (State.Bad, "链接失效");
                default:
                    var msg = ValueStr(token["message"]);
                    if (ContainsAny(msg, "不存在", "失效", "违规", "过期", "取消", "封禁", "审核", "屏蔽", "限制查看"))
                    {
                        return (State.Bad, msg);
                    }
                    if (ContainsAny(msg, "提取码", "密码"))
                    {
                        return (State.Locked, msg);
                    }
                    return (State.Uncertain, msg.Length > 0 ? msg : "无法确认链接状态");
            }

            int tokenStatus = ValueInt(token["status"]);
            if (tokenStatus != 0 && tokenStatus != 200)
            {
                return (State.Bad, Coalesce(ValueStr(token["message"]), "分享链接失效或不存在"));
            }

            var stoken = ValueStr(token["data"]?["stoken"]);
            if (stoken.Length == 0)
            {
                return (State.Uncertain, "访问令牌缺失");
            }

            // Step2: 拉取分享详情，有文件列表即视为有效
            var detailUrl =
                $"https://drive-pc.quark.cn/1/clouddrive/share/sharepage/detail?pwd_id={Uri.EscapeDataString(pwdId)}&stoken={Uri.EscapeDataString(stoken)}&ver=2&pr=ucpro";
            var detailHeaders = new Dictionary<string, string>
            {
                ["origin"] = "https://pan.quark.cn",
                ["referer"] = "https://pan.quark.cn/",
                ["accept"] = "application/json, text/plain, */*",
            };
            var detailResp = await FetchAsync(HttpMethod.Get, detailUrl, null, null, detailHeaders, ct).ConfigureAwait(false);
            if (detailResp.StatusCode == 0)
            {
                return (State.Uncertain, detailResp.Error);
            }
            if (detailResp.Content.Length == 0)
            {
                return (State.Uncertain, "详情请求失败");
            }

            JObject detail;
            try
            {
                detail = JObject.Parse(detailResp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            int detailCode = ValueInt(detail["code"]);
            if (detailCode != 0)
            {
                var detailMsg = Coalesce(ValueStr(detail["message"]), "无法确认链接状态");
                if (ContainsAny(detailMsg, "提取码", "密码", "passcode"))
                {
                    return (State.Locked, detailMsg);
                }
                if (ContainsAny(detailMsg, "不存在", "失效", "违规", "过期", "取消"))
                {
                    return (State.Bad, detailMsg);
                }
                return (State.Uncertain, detailMsg);
            }

            var list = detail["data"]?["list"] as JArray;
            int shareStatus = ValueInt(detail["data"]?["share"]?["status"]);
            bool partialViolation = (bool?)detail["data"]?["share"]?["partial_violation"] ?? false;
            bool isExpire = (bool?)detail["data"]?["is_expire"] ?? false;

            if (list == null || list.Count == 0)
            {
                if (shareStatus > 1 && partialViolation)
                {
                    return (State.Bad, $"分享链接部分违规已失效(share_status={shareStatus})");
                }
                if (shareStatus > 1)
                {
                    return (State.Bad, $"分享链接已失效(share_status={shareStatus})");
                }
                if (isExpire)
                {
                    return (State.Bad, "分享链接已过期");
                }
                return (State.Bad, "分享链接无效：文件列表为空");
            }

            if (shareStatus == 1 && partialViolation)
            {
                return (State.Ok, "链接有效但部分文件违规");
            }
            if (shareStatus == 1)
            {
                return (State.Ok, "链接有效");
            }
            if (shareStatus == 3 && partialViolation)
            {
                return (State.Bad, "分享链接因违规已失效(share_status=3, partial_violation=true)");
            }
            if (shareStatus == 3)
            {
                return (State.Ok, "链接有效");
            }
            if (shareStatus > 1)
            {
                return (State.Bad, $"分享链接已失效(share_status={shareStatus})");
            }
            return partialViolation ? (State.Ok, "链接有效但部分文件违规") : (State.Ok, "链接有效");
        }

        // ---- 百度网盘：有密码先 verify 取 BDCLND cookie，再拉文件列表 ----
        private async Task<(State, string)> CheckBaiduAsync(string normalized, string password, CancellationToken ct)
        {
            var (shareId, shortUrl) = ExtractBaiduShareInfo(normalized);
            if (shareId.Length == 0 || shortUrl.Length == 0)
            {
                return (State.Uncertain, "无法解析分享地址");
            }

            string bdclnd = "";
            if (password.Length > 0)
            {
                var verifyUrl =
                    $"https://pan.baidu.com/share/verify?surl={Uri.EscapeDataString(shortUrl)}&pwd={Uri.EscapeDataString(password)}";
                var verifyForm = $"pwd={Uri.EscapeDataString(password)}&vcode=&vcode_str=";
                var verifyHeaders = new Dictionary<string, string> { ["referer"] = normalized };
                var verifyResp = await FetchAsync(HttpMethod.Post, verifyUrl, null, verifyForm, verifyHeaders, ct).ConfigureAwait(false);
                if (verifyResp.StatusCode == 0)
                {
                    return (State.Uncertain, verifyResp.Error);
                }
                if (verifyResp.Content.Length == 0)
                {
                    return (State.Uncertain, "验证失败");
                }

                JObject verify;
                try
                {
                    verify = JObject.Parse(verifyResp.Content);
                }
                catch (Exception)
                {
                    return (State.Uncertain, "响应解析失败");
                }

                switch (ValueInt(verify["errno"]))
                {
                    case 0:
                        bdclnd = ValueStr(verify["randsk"]);
                        break;
                    case -9:
                    case -12:
                        return (State.Locked, "提取码错误或缺失");
                    default:
                        return (State.Uncertain, Coalesce(ValueStr(verify["errmsg"]), "验证失败"));
                }
            }

            var listUrl =
                $"https://pan.baidu.com/share/list?web=1&page=1&num=20&order=time&desc=1&showempty=0&shorturl={Uri.EscapeDataString(shortUrl)}&root=1&clienttype=0";
            var listHeaders = new Dictionary<string, string>
            {
                ["accept"] = "application/json, text/plain, */*",
                ["referer"] = normalized,
            };
            if (bdclnd.Length > 0)
            {
                listHeaders["cookie"] = "BDCLND=" + bdclnd;
            }
            var listResp = await FetchAsync(HttpMethod.Get, listUrl, null, null, listHeaders, ct).ConfigureAwait(false);
            if (listResp.StatusCode == 0)
            {
                return (State.Uncertain, listResp.Error);
            }
            if (listResp.Content.Length == 0)
            {
                return (State.Uncertain, "请求失败");
            }

            JObject list;
            try
            {
                list = JObject.Parse(listResp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            switch (ValueInt(list["errno"]))
            {
                case 0:
                    var fileList = list["list"] as JArray;
                    return fileList is { Count: > 0 } ? (State.Ok, "链接有效") : (State.Bad, "链接失效");
                case -9:
                case -12:
                    return (State.Locked, "需要提取码");
                case -7:
                case 105:
                case 115:
                case 117:
                case 145:
                    return (State.Bad, "链接失效");
                default:
                    return (State.Uncertain, Coalesce(ValueStr(list["errmsg"]), "无法确认链接状态"));
            }
        }

        // ---- 阿里云盘：匿名共享信息接口，file_count>0 或分享名非空即有效 ----
        private async Task<(State, string)> CheckAliyunAsync(string normalized, string password, CancellationToken ct)
        {
            var shareId = LastPathSegment(normalized);
            if (shareId.Length == 0)
            {
                return (State.Uncertain, "无法解析分享地址");
            }

            var payload = new JObject { ["share_id"] = shareId };
            var headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["origin"] = "https://www.alipan.com",
                ["referer"] = "https://www.alipan.com/",
                ["x-canary"] = "client=web,app=share,version=v2.3.1",
            };
            var resp = await FetchAsync(HttpMethod.Post,
                "https://api.aliyundrive.com/adrive/v3/share_link/get_share_by_anonymous?share_id=" + Uri.EscapeDataString(shareId),
                payload.ToString(), null, headers, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.Content.Length == 0)
            {
                return (State.Uncertain, "请求失败");
            }

            JObject body;
            try
            {
                body = JObject.Parse(resp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            var code = ValueStr(body["code"]);
            if (code.Length > 0)
            {
                var codeLower = code.ToLowerInvariant();
                var message = Coalesce(ValueStr(body["message"]), code);
                if (codeLower.Contains("sharelink"))
                {
                    return (State.Bad, message);
                }
                if (ContainsAny(codeLower, "notfound", "cancelled", "canceled", "forbidden", "expired"))
                {
                    return (State.Bad, message);
                }
                if (ContainsAny(codeLower, "exceed", "frequency", "limit"))
                {
                    return (State.Uncertain, message);
                }
                return (State.Uncertain, message);
            }

            var fileCount = (int?)body["file_count"] ?? 0;
            var shareName = ValueStr(body["share_name"]);
            if (fileCount == 0 && shareName.Length == 0)
            {
                return (State.Bad, "分享内容为空(file_count=0)");
            }

            var shareStatus = ValueStr(body["share_status"]).ToLowerInvariant();
            if (shareStatus.Length > 0 && shareStatus != "enabled" && shareStatus != "normal")
            {
                if (ContainsAny(shareStatus, "forbidden", "cancel", "expired", "illegal", "invalid", "disabled"))
                {
                    return (State.Bad, Coalesce(ValueStr(body["message"]), "链接失效"));
                }
            }

            if (shareName.Length > 0 || ValueStr(body["share_title"]).Length > 0 || fileCount > 0)
            {
                return (State.Ok, "链接有效");
            }
            return (State.Uncertain, Coalesce(ValueStr(body["message"]), "无法确认链接状态"));
        }

        // ---- UC 网盘：GET 分享页按关键词判定（类型分发已限定 host=drive.uc.cn，无 SSRF 面） ----
        private async Task<(State, string)> CheckUcAsync(string normalized, CancellationToken ct)
        {
            var headers = new Dictionary<string, string> { ["user-agent"] = MobileUa };
            var resp = await FetchAsync(HttpMethod.Get, normalized, null, null, headers, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.StatusCode == 404)
            {
                return (State.Bad, "链接失效");
            }

            var page = resp.Content.ToLowerInvariant();
            if (ContainsAny(page, "失效", "不存在", "违规", "删除", "已过期", "被取消"))
            {
                return (State.Bad, "链接失效");
            }
            if (ContainsAny(page, "提取码", "访问码", "请输入密码"))
            {
                return (State.Locked, "需要提取码");
            }
            if (ContainsAny(page, "文件", "分享", "drive.uc.cn"))
            {
                return (State.Ok, "链接有效");
            }
            return (State.Uncertain, "无法确认链接状态");
        }

        // ---- 天翼云盘：getShareInfoByCodeV2 接口，shareId 有效/文件名非空/需访问码即视为有效 ----
        private async Task<(State, string)> CheckTianyiAsync(string normalized, string password, CancellationToken ct)
        {
            var shareCode = ExtractTianyiShareCode(normalized);
            if (shareCode.Length == 0)
            {
                return (State.Uncertain, "无法解析分享地址");
            }

            // 天翼访问码需以「（访问码：xxxx）」形式拼进 shareCode 参数
            var shareCodeParam = shareCode;
            if (password.Length > 0)
            {
                shareCodeParam = $"{shareCode}（访问码：{password}）";
            }

            var noCache = DateTime.Now.Ticks.ToString();
            var apiUrl =
                $"https://cloud.189.cn/api/open/share/getShareInfoByCodeV2.action?noCache={noCache}&shareCode={Uri.EscapeDataString(shareCodeParam)}";
            var headers = new Dictionary<string, string>
            {
                ["referer"] = normalized,
                ["sign-type"] = "1",
            };
            var resp = await FetchAsync(HttpMethod.Get, apiUrl, null, null, headers, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.Content.Length == 0)
            {
                return (State.Uncertain, "请求失败");
            }

            // 优先解析天翼的 XML 响应（shareVO / error），兼容 JSON 响应
            var content = resp.Content;
            var shareIdM = TianyiShareIdRegex.Match(content);
            var fileNameM = TianyiFileNameRegex.Match(content);
            var needCodeM = TianyiNeedCodeRegex.Match(content);

            if ((long.TryParse(shareIdM.Groups[1].Value, out var shareId) && shareId > 0) ||
                (fileNameM.Success && !string.IsNullOrWhiteSpace(fileNameM.Groups[1].Value)) ||
                (needCodeM.Success && needCodeM.Groups[1].Value == "1"))
            {
                return (State.Ok, "链接有效");
            }

            var lower = content.ToLowerInvariant();
            if (ContainsAny(lower, "accesscode", "needaccesscode", "访问码", "提取码", "密码"))
            {
                return (State.Locked, "需要访问码");
            }

            foreach (var err in new[] { "ShareInfoNotFound", "ShareNotFound", "FileNotFound", "ShareExpiredError", "ShareAuditNotPass", "FolderNotFound" })
            {
                if (content.Contains(err, StringComparison.OrdinalIgnoreCase))
                {
                    return (State.Bad, MapTianyiError(err));
                }
            }
            if (ContainsAny(lower, "不存在", "失效", "取消", "过期"))
            {
                return (State.Bad, "链接失效");
            }
            return (State.Uncertain, "无法确认链接状态");
        }

        // ---- 123 网盘：share/info 接口；HTTP 403 多为反爬拦截，视为有效 ----
        private async Task<(State, string)> CheckPan123Async(string normalized, CancellationToken ct)
        {
            var shareKey = LastPathSegment(normalized);
            if (shareKey.Length == 0)
            {
                return (State.Uncertain, "无法解析分享地址");
            }

            var apiUrl = $"https://www.123pan.com/api/share/info?shareKey={Uri.EscapeDataString(shareKey)}";
            var resp = await FetchAsync(HttpMethod.Get, apiUrl, null, null, null, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.StatusCode == 403)
            {
                return (State.Ok, "链接有效");
            }
            if (resp.Content.Length == 0)
            {
                return (State.Uncertain, "响应解析失败");
            }

            JObject body;
            try
            {
                body = JObject.Parse(resp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            if (ValueInt(body["code"]) == 0)
            {
                return (State.Ok, "链接有效");
            }
            if ((bool?)body["data"]?["HasPwd"] ?? false)
            {
                return (State.Locked, "需要提取码");
            }
            var msg = ValueStr(body["message"]);
            return msg.Length > 0 ? (State.Bad, msg) : (State.Bad, "链接失效");
        }

        // ---- 迅雷网盘：可选 captcha init，再查 share 状态 ----
        private async Task<(State, string)> CheckXunleiAsync(string normalized, string password, CancellationToken ct)
        {
            var m = XunleiShareRegex.Match(normalized);
            if (!m.Success)
            {
                return (State.Uncertain, "无法解析分享地址");
            }
            var shareId = m.Groups[1].Value;

            var captchaToken = await FetchXunleiCaptchaTokenAsync(ct).ConfigureAwait(false);

            var apiUrl =
                $"https://api-pan.xunlei.com/drive/v1/share?share_id={Uri.EscapeDataString(shareId)}&pass_code={Uri.EscapeDataString(password)}&limit=100&pass_code_token=&page_token=&thumbnail_size=SIZE_SMALL";
            var headers = new Dictionary<string, string>
            {
                ["accept"] = "*/*",
                ["origin"] = "https://pan.xunlei.com",
                ["referer"] = "https://pan.xunlei.com/",
                ["user-agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
                ["x-client-id"] = XunleiClientId,
                ["x-device-id"] = XunleiDeviceId,
            };
            if (captchaToken.Length > 0)
            {
                headers["x-captcha-token"] = captchaToken;
            }
            var resp = await FetchAsync(HttpMethod.Get, apiUrl, null, null, headers, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.StatusCode == 404 || resp.StatusCode == 403)
            {
                return (State.Bad, "链接失效");
            }
            if (resp.Content.Length == 0)
            {
                return (State.Uncertain, "响应解析失败");
            }

            JObject body;
            try
            {
                body = JObject.Parse(resp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            var shareStatus = ValueStr(body["share_status"]);
            var error = ValueStr(body["error"]);
            var errorMsg = ValueStr(body["error_description"]);
            var shareStatusText = ValueStr(body["share_status_text"]);

            if (shareStatus.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                ValueStr(body["share_name"]).Length > 0 ||
                ValueInt(body["file_count"]) > 0 ||
                ValueStr(body["share_id"]).Length > 0)
            {
                return (State.Ok, "链接有效");
            }
            if (ContainsAny(error, "pass_code") || ContainsAny(errorMsg, "pass_code", "提取码", "密码") ||
                ContainsAny(shareStatus, "pass_code") || ContainsAny(shareStatusText, "pass_code", "提取码", "密码"))
            {
                return (State.Locked, Coalesce(errorMsg, shareStatusText, "需要提取码"));
            }
            if (shareStatus.Length > 0)
            {
                return (State.Bad, Coalesce(shareStatusText, $"分享状态: {shareStatus}"));
            }
            if (ValueInt(body["error_code"]) != 0)
            {
                if (ContainsAny(errorMsg, "参数错误", "share_status", "不存在", "失效", "过期", "not found"))
                {
                    return (State.Bad, Coalesce(errorMsg, "链接失效"));
                }
                return (State.Uncertain, Coalesce(errorMsg, error, "无法确认链接状态"));
            }
            return (State.Uncertain, "无法确认链接状态");
        }

        // ---- 115：无提取码直接判锁定；有密码走 share/snap 查询 ----
        private async Task<(State, string)> CheckPan115Async(string normalized, string password, CancellationToken ct)
        {
            var shareCode = LastPathSegment(normalized);
            if (shareCode.Length == 0)
            {
                return (State.Uncertain, "无法解析分享地址");
            }
            if (password.Length == 0)
            {
                return (State.Locked, "115 需要提取码");
            }

            var apiUrl =
                $"https://115cdn.com/webapi/share/snap?share_code={Uri.EscapeDataString(shareCode)}&offset=0&limit=20&receive_code={Uri.EscapeDataString(password)}&cid=";
            var headers = new Dictionary<string, string>
            {
                ["priority"] = "u=1, i",
                ["referer"] = $"https://115cdn.com/s/{shareCode}?password={password}&",
                ["x-requested-with"] = "XMLHttpRequest",
            };
            var resp = await FetchAsync(HttpMethod.Get, apiUrl, null, null, headers, ct).ConfigureAwait(false);
            if (resp.StatusCode == 0)
            {
                return (State.Uncertain, resp.Error);
            }
            if (resp.Content.Length == 0)
            {
                return (State.Uncertain, "响应解析失败");
            }

            JObject body;
            try
            {
                body = JObject.Parse(resp.Content);
            }
            catch (Exception)
            {
                return (State.Uncertain, "响应解析失败");
            }

            bool state = (bool?)body["state"] ?? false;
            var error = ValueStr(body["error"]);
            int errno = ValueInt(body["errno"]);

            if (state && errno == 0)
            {
                int shareState = ValueInt(body["data"]?["share_state"]);
                if (shareState == 0)
                {
                    shareState = ValueInt(body["data"]?["shareinfo"]?["share_state"]);
                }
                var reason = ValueStr(body["data"]?["shareinfo"]?["forbid_reason"]).Trim();
                if (reason.Length == 0 && (shareState == 0 || shareState == 1))
                {
                    var dataList = body["data"]?["list"] as JArray;
                    if (shareState == 1 || (dataList != null && dataList.Count > 0) ||
                        ValueInt(body["data"]?["count"]) > 0 ||
                        ValueStr(body["data"]?["shareinfo"]?["snap_id"]).Length > 0 ||
                        ValueStr(body["data"]?["shareinfo"]?["share_title"]).Length > 0)
                    {
                        return (State.Ok, "链接有效");
                    }
                }
                if (reason.Length == 0)
                {
                    reason = $"链接状态异常(share_state={shareState})";
                }
                if (ContainsAny(reason, "密码", "提取码"))
                {
                    return (State.Locked, reason);
                }
                return (State.Bad, reason);
            }

            if (ContainsAny(error, "密码", "提取码", "receive_code"))
            {
                return (State.Locked, Coalesce(error, "需要提取码"));
            }
            if (ContainsAny(error, "参数错误", "不存在", "失效", "share_code", "forbid", "forbidden", "违规", "删除", "取消"))
            {
                return (State.Bad, Coalesce(error, "链接失效"));
            }
            return error.Length > 0 ? (State.Bad, error) : (State.Uncertain, "无法确认链接状态");
        }

        // =====================================================================
        //    基础设施：HTTP 请求 / 结果构建 / 缓存
        // =====================================================================

        private readonly struct HttpResult
        {
            public int StatusCode { get; init; }
            public string Content { get; init; }
            public string Error { get; init; }
        }

        /// <summary>
        /// 统一探测请求入口：仅请求平台官方固定域名（由各探测方法自行拼装目标 URL），
        /// 绝不直接请求用户传入的原始链接（UC 是唯一例外，但类型分发已限定 host=drive.uc.cn），杜绝 SSRF。
        /// </summary>
        private async Task<HttpResult> FetchAsync(HttpMethod method, string url, string jsonBody, string formBody,
            IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (jsonBody != null)
                {
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }
                else if (formBody != null)
                {
                    request.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
                }
                if (headers != null)
                {
                    foreach (var (k, v) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(k, v);
                    }
                }

                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var bytes = await ReadLimitedAsync(resp.Content, 1_000_000, ct).ConfigureAwait(false);
                return new HttpResult { StatusCode = (int)resp.StatusCode, Content = Encoding.UTF8.GetString(bytes) };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new HttpResult { StatusCode = 0, Content = "", Error = "请求超时" };
            }
            catch (Exception ex)
            {
                return new HttpResult { StatusCode = 0, Content = "", Error = ex.Message };
            }
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream(Math.Min(maxBytes, 81920));
            var buffer = new byte[81920];
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
                await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            return ms.ToArray();
        }

        private static PanLinkCheckResult Build(string originalUrl, string type, State state, string summary)
        {
            // locked 表示链接真实存在但需要提取码：Valid 仍为 true，不误杀可用链接
            bool? valid = state switch
            {
                State.Ok => true,
                State.Bad => false,
                State.Locked => true,
                _ => null,
            };
            return new PanLinkCheckResult { Url = originalUrl, Type = type, Valid = valid, Message = summary };
        }

        private static string BuildCacheKey(string type, string normalized, string password)
            => type + "|" + normalized + "|" + password;

        private static TimeSpan TtlFor(State state) => state switch
        {
            State.Ok or State.Unsupported => TimeSpan.FromHours(24),
            State.Bad => TimeSpan.FromHours(6),
            State.Locked => TimeSpan.FromHours(12),
            _ => TimeSpan.FromMinutes(30),
        };

        // ---- 链接标准化与分享码提取 ----
        private static string NormalizeShareLink(string raw, string password, string type)
        {
            var url = NetDiskDetector.NormalizeUrl(raw);
            if (url.Length == 0)
            {
                return "";
            }
            // host 统一小写，避免缓存键大小写分叉导致重复探测
            url = Regex.Replace(url, @"^(https?://)([^/]+)",
                m => m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant(), RegexOptions.IgnoreCase);
            // 百度/夸克/UC 注入 pwd 参数，让缓存键带上密码差异
            if (password.Length > 0 && (type == CloudTypes.Baidu || type == CloudTypes.Quark || type == CloudTypes.Uc))
            {
                if (!Regex.IsMatch(url, @"[?&#]pwd=", RegexOptions.IgnoreCase))
                {
                    url += (url.Contains('?') ? "&" : "?") + "pwd=" + Uri.EscapeDataString(password);
                }
            }
            return url;
        }

        private static string ResolvePassword(string rawUrl, string explicitPassword, string type)
        {
            if (!string.IsNullOrWhiteSpace(explicitPassword))
            {
                return explicitPassword.Trim();
            }
            // 从 URL 内嵌的 pwd=/password=/编码访问码 中提取
            return NetDiskDetector.ExtractPassword("", rawUrl);
        }

        private static (string ShareId, string ShortUrl) ExtractBaiduShareInfo(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return ("", "");
            }
            if (parsed.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            {
                var shareId = parsed.AbsolutePath.Substring(3);
                var shortUrl = shareId.StartsWith("1") && shareId.Length > 1 ? shareId.Substring(1) : shareId;
                return (shareId, shortUrl);
            }
            if (parsed.AbsolutePath.StartsWith("/share/init", StringComparison.OrdinalIgnoreCase))
            {
                var m = BaiduSurlParamRegex.Match(url);
                if (m.Success)
                {
                    var sid = m.Groups[1].Value;
                    var shortUrl = sid.StartsWith("1") && sid.Length > 1 ? sid.Substring(1) : sid;
                    return (sid, shortUrl);
                }
            }
            return ("", "");
        }

        private static string LastPathSegment(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return "";
            }
            var path = parsed.AbsolutePath.Trim('/');
            if (path.Length == 0)
            {
                return "";
            }
            var parts = path.Split('/');
            return parts[^1];
        }

        private static string ExtractTianyiShareCode(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return "";
            }
            var qIndex = url.IndexOf('?');
            if (qIndex >= 0)
            {
                var m = Regex.Match(url.Substring(qIndex + 1), @"(?:^|[?&])code=([^&#]+)");
                if (m.Success)
                {
                    return Uri.UnescapeDataString(m.Groups[1].Value);
                }
            }
            if (parsed.AbsolutePath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
            {
                var sc = parsed.AbsolutePath.Substring(3);
                var slash = sc.IndexOf('/');
                return slash >= 0 ? sc.Substring(0, slash) : sc;
            }
            return "";
        }

        private static string MapTianyiError(string code) => code switch
        {
            "ShareInfoNotFound" => "分享信息不存在",
            "ShareNotFound" => "分享链接不存在",
            "FileNotFound" => "分享文件不存在",
            "ShareExpiredError" => "分享链接已过期",
            "ShareAuditNotPass" => "分享因审核未通过已失效",
            "FolderNotFound" => "分享文件夹不存在",
            _ => "链接失效",
        };

        // ---- 迅雷 captcha token（失败静默降级：不带 token 直接探测） ----
        private async Task<string> FetchXunleiCaptchaTokenAsync(CancellationToken ct)
        {
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                var signature = BuildXunleiCaptchaSignature(
                    XunleiClientId, XunleiClientVersion, XunleiPackageName, XunleiDeviceId, timestamp);
                var payload = new JObject
                {
                    ["action"] = "get:/drive/v1/share",
                    ["captcha_token"] = "",
                    ["client_id"] = XunleiClientId,
                    ["device_id"] = XunleiDeviceId,
                    ["meta"] = new JObject
                    {
                        ["timestamp"] = timestamp,
                        ["captcha_sign"] = signature,
                        ["client_version"] = XunleiClientVersion,
                        ["package_name"] = XunleiPackageName,
                    },
                    ["redirect_uri"] = "xlaccsdk01://xunlei.com/callback?state=harbor",
                };
                var headers = new Dictionary<string, string>
                {
                    ["accept"] = "application/json;charset=UTF-8",
                    ["x-device-id"] = XunleiDeviceId,
                    ["x-client-id"] = XunleiClientId,
                    ["x-client-version"] = XunleiClientVersion,
                };
                var resp = await FetchAsync(HttpMethod.Post, "https://xluser-ssl.xunlei.com/v1/shield/captcha/init",
                    payload.ToString(), null, headers, ct).ConfigureAwait(false);
                if (resp.StatusCode == 0 || resp.Content.Length == 0)
                {
                    return "";
                }
                var body = JObject.Parse(resp.Content);
                // 返回了验证 URL 说明需过二次验证，此时不携带 token 直接探测
                if (ValueStr(body["url"]).Length > 0)
                {
                    return "";
                }
                return ValueStr(body["captcha_token"]);
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string BuildXunleiCaptchaSignature(string clientId, string clientVersion,
            string packageName, string deviceId, string timestamp)
        {
            string content = clientId + clientVersion + packageName + deviceId + timestamp;
            foreach (var seed in XunleiSignatureSeeds)
            {
                content = Md5Hex(content + seed);
            }
            return "1." + content;
        }

        private static string Md5Hex(string input)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexStringLower(bytes);
        }

        // ---- JSON / 文本小工具 ----
        private static int ValueInt(JToken token) => (int?)token ?? 0;
        private static string ValueStr(JToken token) => token?.ToString() ?? "";
        private static string Coalesce(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }
            return "";
        }

        private static bool ContainsAny(string content, params string[] keywords)
        {
            foreach (var k in keywords)
            {
                if (content.Contains(k, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}