using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AggPanSearch.Business.PanManage.Quark
{
    /// <summary>
    /// 描 述：夸克云盘接口异常。
    /// Code 为夸克返回的业务码（0 表示网络/解析层错误）；
    /// CookieInvalid 为 true 表示登录态失效，调用方应中止整批任务并提示更新 Cookie。
    /// </summary>
    public sealed class QuarkPanException : Exception
    {
        /// <summary>夸克"未登录/登录态失效"业务码。</summary>
        public const int CodeCookieInvalid = 31001;

        public QuarkPanException(int code, string message, bool cookieInvalid = false)
            : base(message)
        {
            Code = code;
            CookieInvalid = cookieInvalid;
        }

        public int Code { get; }

        public bool CookieInvalid { get; }
    }

    /// <summary>
    /// 描 述：从夸克分享链接中解析出的转存参数。
    /// 支持 https://pan.quark.cn/s/{pwd_id} 以及带 ?pwd=xxxx 提取码的形式。
    /// </summary>
    public sealed class QuarkShareLink
    {
        private static readonly Regex SharePattern = new Regex(
            @"^https?://(?:pan|drive)\.quark\.cn/s/(?<id>[A-Za-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PwdPattern = new Regex(
            @"[?&#]pwd=(?<pwd>[A-Za-z0-9]{1,8})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>分享标识（链接 /s/ 后的部分）。</summary>
        public string PwdId { get; private set; } = string.Empty;

        /// <summary>提取码，无提取码时为空字符串。</summary>
        public string Passcode { get; private set; } = string.Empty;

        /// <summary>原始分享链接，用于判重与留档。</summary>
        public string RawUrl { get; private set; } = string.Empty;

        public static bool TryParse(string url, out QuarkShareLink link, out string error)
        {
            return TryParse(url, string.Empty, out link, out error);
        }

        /// <summary>
        /// 解析分享链接：URL 自带 ?pwd=xxxx 时优先使用链接中的提取码，
        /// 否则使用调用方传入的 fallbackPasscode（后台编辑弹窗里填写的「提取码」）。
        /// </summary>
        public static bool TryParse(string url, string fallbackPasscode, out QuarkShareLink link, out string error)
        {
            link = new QuarkShareLink();
            error = string.Empty;
            string text = (url ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "资源链接为空";
                return false;
            }

            Match match = SharePattern.Match(text);
            if (!match.Success)
            {
                error = "非夸克网盘分享链接（仅支持 pan.quark.cn/s/ 开头的地址）";
                return false;
            }

            Match pwd = PwdPattern.Match(text);
            link.PwdId = match.Groups["id"].Value;
            link.Passcode = pwd.Success
                ? pwd.Groups["pwd"].Value
                : (fallbackPasscode ?? string.Empty).Trim();
            link.RawUrl = text;
            return true;
        }
    }

    /// <summary>分享 token 接口返回的分享信息。</summary>
    public sealed class QuarkShareToken
    {
        public string Stoken { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>分享者昵称，可能为空。</summary>
        public string AuthorName { get; set; } = string.Empty;
    }

    /// <summary>分享内的单个文件/目录条目。</summary>
    public sealed class QuarkShareFile
    {
        public string Fid { get; set; } = string.Empty;

        public string ShareFidToken { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public bool IsDir { get; set; }

        public long Size { get; set; }
    }

    /// <summary>转存任务（task 接口）的轮询结果。</summary>
    public sealed class QuarkTaskResult
    {
        /// <summary>转存后的顶层文件/目录 fid（用于创建分享）。</summary>
        public List<string> TopFids { get; set; } = new List<string>();

        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 描 述：夸克网盘接口客户端（只做 HTTP 与协议解析，不感知业务与数据库）。
    /// 所有请求共用静态 HttpClient（连接池复用、默认直连不走系统代理），
    /// Cookie 由调用方传入并逐请求写入，避免多任务串味。
    /// </summary>
    public sealed class QuarkPanClient
    {
        private const string BaseUrl = "https://drive-pc.quark.cn/1/clouddrive";

        private const string DefaultQuery = "pr=ucpro&fr=pc&uc_param_str=";

        private const string Origin = "https://pan.quark.cn";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        /// <summary>转存任务轮询总超时；大文件转存耗时较长，默认 30 分钟，可用环境变量覆盖（单位：分钟）。</summary>
        private static readonly TimeSpan TransferTimeout = ResolveTransferTimeout();

        /// <summary>创建分享任务轮询总超时（服务端通常 1 秒内完成）。</summary>
        private static readonly TimeSpan ShareTaskTimeout = TimeSpan.FromSeconds(60);

        private static readonly HttpClient Client = CreateClient();

        /// <summary>粘贴 Cookie 时常见的脏字符：换行、制表、不间断空格、零宽字符、BOM。</summary>
        private static readonly Regex CookieDirtyChars = new Regex(
            @"[\s\u00A0\u200B-\u200D\uFEFF]+",
            RegexOptions.Compiled);

        private readonly string cookie;

        public QuarkPanClient(string cookie)
        {
            this.cookie = NormalizeCookie(cookie);
        }

        /// <summary>
        /// 规范化 Cookie 输入，兼容四种粘贴形态：
        /// 1) 请求头原样（推荐）：<c>__puus=xxx; __pus=yyy</c>
        /// 2) JSON 数组（DevTools / 插件导出）：<c>[{"name":"__puus","value":"xxx","domain":".quark.cn"}]</c>
        /// 3) JSON 对象：<c>{"__puus":"xxx"}</c> 或 <c>{"cookies":[...]}</c>
        /// 4) DevTools Application→Cookies 表格复制（tab 分隔多行）
        /// 统一输出为可直接写入请求头的一行字符串；去空白/全角标点是为了避免网关判为非法请求（HTTP 400）。
        /// </summary>
        private static string NormalizeCookie(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }
            if (TryConvertJsonCookie(text, out string fromJson))
            {
                return fromJson;
            }
            if (TryConvertTableCookie(text, out string fromTable))
            {
                return fromTable;
            }
            if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(7);
            }
            text = CookieDirtyChars.Replace(text, string.Empty);
            // 中文输入法下的全角分号/逗号
            text = text.Replace('；', ';').Replace('，', ',');
            return text.Trim();
        }

        /// <summary>把插件/DveTools 导出的 JSON 形式 Cookie 转成请求头字符串；不是 JSON 时返回 false。</summary>
        private static bool TryConvertJsonCookie(string text, out string cookie)
        {
            cookie = string.Empty;
            if (text[0] != '{' && text[0] != '[')
            {
                return false;
            }

            JToken token;
            try
            {
                token = JToken.Parse(text);
            }
            catch (Exception)
            {
                return false;
            }

            List<CookiePair> pairs = new List<CookiePair>();
            CollectCookiePairs(token, pairs);
            return BuildCookieHeader(pairs, out cookie);
        }

        /// <summary>解析 DevTools → Application → Cookies 表格复制出来的多行 tab 分隔文本。</summary>
        private static bool TryConvertTableCookie(string text, out string cookie)
        {
            cookie = string.Empty;
            if (text.IndexOf('\t') < 0 || text.IndexOf('\n') < 0)
            {
                return false;
            }

            List<CookiePair> pairs = new List<CookiePair>();
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            foreach (string line in lines)
            {
                string[] cells = line.Split('\t');
                if (cells.Length < 2)
                {
                    continue;
                }

                string name = cells[0].Trim();
                string value = cells[1].Trim();
                // 表头行（Name / 名称）与明显无效的行直接跳过
                if (name.Length == 0 || value.Length == 0
                    || string.Equals(name, "name", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "名称", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string domain = string.Empty;
                foreach (string cell in cells)
                {
                    if (cell.IndexOf("quark", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        domain = cell.Trim();
                        break;
                    }
                }
                pairs.Add(new CookiePair { Name = name, Value = value, Domain = domain });
            }
            return BuildCookieHeader(pairs, out cookie);
        }

        /// <summary>把解析出的 Cookie 键值拼成请求头；混入其它站点时优先保留夸克域。</summary>
        private static bool BuildCookieHeader(List<CookiePair> pairs, out string cookie)
        {
            cookie = string.Empty;
            if (pairs.Count == 0)
            {
                return false;
            }

            List<CookiePair> quarkPairs = pairs
                .Where(p => p.Domain.IndexOf("quark", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (quarkPairs.Count > 0)
            {
                pairs = quarkPairs;
            }

            cookie = string.Join("; ", pairs
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First().Name + "=" + g.First().Value));
            return cookie.Length > 0;
        }

        private static void CollectCookiePairs(JToken token, List<CookiePair> pairs)
        {
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    CollectCookiePairs(item, pairs);
                }
                return;
            }

            if (!(token is JObject obj))
            {
                return;
            }

            // EditThisCookie 等插件导出：{"cookies":[{...}]}
            JProperty cookiesNode = FindProperty(obj, "cookies");
            if (cookiesNode != null && cookiesNode.Value is JArray)
            {
                CollectCookiePairs(cookiesNode.Value, pairs);
                return;
            }

            // 单项：{"name":"__puus","value":"xxx","domain":".quark.cn"}
            string name = ReadJsonString(obj, "name");
            if (name.Length > 0)
            {
                pairs.Add(new CookiePair
                {
                    Name = name,
                    Value = ReadJsonString(obj, "value"),
                    Domain = ReadJsonString(obj, "domain"),
                });
                return;
            }

            // 键值字典：{"__puus":"xxx","__pus":"yyy"}
            foreach (JProperty property in obj.Properties())
            {
                if (property.Value.Type == JTokenType.Object || property.Value.Type == JTokenType.Array)
                {
                    continue;
                }
                string value = property.Value.ToString();
                if (value.Length == 0)
                {
                    continue;
                }
                pairs.Add(new CookiePair { Name = property.Name, Value = value });
            }
        }

        private static string ReadJsonString(JObject obj, string key)
        {
            JProperty property = FindProperty(obj, key);
            return property == null || property.Value.Type == JTokenType.Null
                ? string.Empty
                : property.Value.ToString();
        }

        /// <summary>忽略大小写查找 JSON 属性（兼容不同插件导出的字段名）。</summary>
        private static JProperty FindProperty(JObject obj, string key)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property;
                }
            }
            return null;
        }

        /// <summary>JSON / 表格形态解析出的一条 Cookie。</summary>
        private sealed class CookiePair
        {
            public string Name { get; set; } = string.Empty;

            public string Value { get; set; } = string.Empty;

            public string Domain { get; set; } = string.Empty;
        }

        /// <summary>是否已配置 Cookie（为空时所有接口调用都会抛出 CookieInvalid 异常）。</summary>
        public bool HasCookie
        {
            get { return cookie.Length > 0; }
        }

        private static HttpClient CreateClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                // Cookie 逐请求手动写入，关闭自动 Cookie 容器避免任务之间互相影响
                UseCookies = false,
                // 默认直连，避免继承本机系统代理（代理出口常被夸克判定为异常来源）
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                MaxConnectionsPerServer = 4,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
        }

        private static TimeSpan ResolveTransferTimeout()
        {
            string value = Environment.GetEnvironmentVariable("QUARK_TRANSFER_TIMEOUT_MINUTES");
            if (int.TryParse(value, out int minutes) && minutes >= 1 && minutes <= 180)
            {
                return TimeSpan.FromMinutes(minutes);
            }
            return TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// 校验 Cookie 是否可用（GET config），返回账号信息摘要（会员类型 / 容量），失败抛异常。
        /// </summary>
        public async Task<string> CheckCookieAsync(CancellationToken token)
        {
            if (!HasCookie)
            {
                throw new QuarkPanException(0, "未配置夸克 Cookie", true);
            }
            if (!cookie.Contains("__puus", StringComparison.OrdinalIgnoreCase))
            {
                throw new QuarkPanException(
                    0,
                    "Cookie 缺少登录态字段 __puus。请浏览器登录 pan.quark.cn → F12 → Network → 点任意 drive-pc.quark.cn 请求 → Request Headers → 复制 Cookie 整行为字符串（形如 __puus=...; __pus=...; __kp=...），不要填 JSON 文件内容",
                    true);
            }
            JObject data = await GetAsync("config", DefaultQuery, token).ConfigureAwait(false);
            string memberType = data.Value<string>("member_type") ?? string.Empty;
            long total = data.Value<long?>("total_capacity") ?? 0;
            long used = data.Value<long?>("use_capacity") ?? 0;
            if (total <= 0)
            {
                return memberType.Length > 0 ? "会员类型：" + memberType : "Cookie 可用";
            }
            return string.Format("会员类型：{0}，容量 {1:0.#} GB / 已用 {2:0.#} GB",
                memberType.Length > 0 ? memberType : "普通用户",
                total / 1024d / 1024d / 1024d,
                used / 1024d / 1024d / 1024d);
        }

        /// <summary>用分享标识与提取码换取 stoken。</summary>
        public async Task<QuarkShareToken> GetShareTokenAsync(QuarkShareLink link, CancellationToken token)
        {
            JObject body = new JObject
            {
                ["pwd_id"] = link.PwdId,
                ["passcode"] = link.Passcode ?? string.Empty,
            };
            JObject data = await PostAsync("share/sharepage/token", DefaultQuery, body, token).ConfigureAwait(false);
            string stoken = data.Value<string>("stoken") ?? string.Empty;
            if (stoken.Length == 0)
            {
                throw new QuarkPanException(0, "获取分享 token 失败，链接可能已失效");
            }
            return new QuarkShareToken
            {
                Stoken = stoken,
                Title = data.Value<string>("title") ?? string.Empty,
                AuthorName = data.Value<string>("author_name") ?? string.Empty,
            };
        }

        /// <summary>读取分享根目录（pdir_fid=0）下的文件/目录清单。</summary>
        public Task<List<QuarkShareFile>> GetShareFilesAsync(QuarkShareLink link, string stoken, CancellationToken token)
        {
            return GetShareFilesAsync(link, stoken, "0", token);
        }

        /// <summary>
        /// 读取分享内指定目录（pdirFid）下的文件/目录清单。
        /// 分享常见形态是「一个大目录包着所有文件」，需要下钻一层时使用该重载。
        /// </summary>
        public async Task<List<QuarkShareFile>> GetShareFilesAsync(QuarkShareLink link, string stoken, string pdirFid, CancellationToken token)
        {
            List<QuarkShareFile> result = new List<QuarkShareFile>();
            string parentFid = string.IsNullOrWhiteSpace(pdirFid) ? "0" : pdirFid.Trim();
            const int pageSize = 50;
            for (int page = 1; page <= 4; page++)
            {
                string query = DefaultQuery
                    + "&pwd_id=" + Uri.EscapeDataString(link.PwdId)
                    + "&stoken=" + Uri.EscapeDataString(stoken)
                    + "&pdir_fid=" + Uri.EscapeDataString(parentFid) + "&force=0&_page=" + page
                    + "&_size=" + pageSize
                    + "&_fetch_total=1&_sort=file_type:asc,updated_at:desc";
                JObject data = await GetAsync("share/sharepage/detail", query, token).ConfigureAwait(false);
                JArray list = data["list"] as JArray;
                if (list == null || list.Count == 0)
                {
                    break;
                }
                foreach (JToken item in list)
                {
                    result.Add(ToShareFile(item));
                }
                if (list.Count < pageSize)
                {
                    break;
                }
            }
            return result;
        }

        private static QuarkShareFile ToShareFile(JToken item)
        {
            return new QuarkShareFile
            {
                Fid = item.Value<string>("fid") ?? string.Empty,
                ShareFidToken = item.Value<string>("share_fid_token") ?? string.Empty,
                FileName = item.Value<string>("file_name") ?? string.Empty,
                IsDir = IsDirToken(item),
                Size = item.Value<long?>("size") ?? 0,
            };
        }

        /// <summary>判断条目是否目录：优先看 dir 布尔字段，其次 file_type=0。</summary>
        private static bool IsDirToken(JToken item)
        {
            bool? dir = item.Value<bool?>("dir");
            if (dir != null)
            {
                return dir.Value;
            }
            int? fileType = item.Value<int?>("file_type");
            return fileType != null && fileType.Value == 0;
        }

        /// <summary>
        /// 在网盘根目录下确保存在指定名称的文件夹，返回其 fid：已存在直接复用，不存在则创建。
        /// </summary>
        public Task<string> EnsureTopFolderAsync(string folderName, CancellationToken token)
        {
            return EnsureChildFolderAsync("0", folderName, token);
        }

        /// <summary>
        /// 在指定父目录下确保存在指定名称的文件夹，返回其 fid：已存在直接复用，不存在则创建。
        /// parentFid 为 "0" 时表示网盘根目录。
        /// </summary>
        public async Task<string> EnsureChildFolderAsync(string parentFid, string folderName, CancellationToken token)
        {
            string pdirFid = string.IsNullOrWhiteSpace(parentFid) ? "0" : parentFid.Trim();
            string name = (folderName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                throw new QuarkPanException(0, "目标文件夹名称为空");
            }

            string fid = await FindChildFolderAsync(pdirFid, name, token).ConfigureAwait(false);
            if (fid.Length > 0)
            {
                return fid;
            }

            JObject body = new JObject
            {
                ["pdir_fid"] = pdirFid,
                ["file_name"] = name,
                ["dir_path"] = string.Empty,
                ["dir_init_lock"] = false,
            };
            JObject data = await PostAsync("file", DefaultQuery, body, token).ConfigureAwait(false);
            fid = data.Value<string>("fid") ?? string.Empty;
            if (fid.Length == 0)
            {
                throw new QuarkPanException(0, "创建网盘文件夹「" + name + "」失败");
            }
            return fid;
        }

        /// <summary>在指定父目录下按名称精确查找文件夹，找不到返回空字符串。</summary>
        private async Task<string> FindChildFolderAsync(string parentFid, string folderName, CancellationToken token)
        {
            const int pageSize = 100;
            for (int page = 1; page <= 5; page++)
            {
                string query = DefaultQuery
                    + "&pdir_fid=" + Uri.EscapeDataString(parentFid) + "&_page=" + page
                    + "&_size=" + pageSize
                    + "&_fetch_total=1&_sort=file_type:asc,file_name:asc";
                JObject data = await GetAsync("file/sort", query, token).ConfigureAwait(false);
                JArray list = data["list"] as JArray;
                if (list == null || list.Count == 0)
                {
                    return string.Empty;
                }
                foreach (JToken item in list)
                {
                    if (IsDirToken(item)
                        && string.Equals(item.Value<string>("file_name"), folderName, StringComparison.Ordinal))
                    {
                        return item.Value<string>("fid") ?? string.Empty;
                    }
                }
                if (list.Count < pageSize)
                {
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 把分享内的文件转存到目标目录，返回转存任务 id（需再调用 <see cref="WaitTaskAsync"/> 等待完成）。
        /// </summary>
        public async Task<string> SaveAsync(
            QuarkShareLink link,
            string stoken,
            string toPdirFid,
            List<QuarkShareFile> files,
            CancellationToken token)
        {
            if (files == null || files.Count == 0)
            {
                throw new QuarkPanException(0, "分享内容为空，无法转存");
            }
            JObject body = new JObject
            {
                ["fid_list"] = new JArray(files.Select(t => t.Fid)),
                ["fid_token_list"] = new JArray(files.Select(t => t.ShareFidToken)),
                ["to_pdir_fid"] = toPdirFid,
                ["pwd_id"] = link.PwdId,
                ["stoken"] = stoken,
                ["pdir_fid"] = "0",
                ["scene"] = "link",
            };
            JObject data = await PostAsync("share/sharepage/save", DefaultQuery, body, token).ConfigureAwait(false);
            string taskId = data.Value<string>("task_id") ?? string.Empty;
            if (taskId.Length == 0)
            {
                throw new QuarkPanException(0, "提交转存任务失败（未返回 task_id）");
            }
            return taskId;
        }

        /// <summary>
        /// 轮询转存任务直到完成：间隔从 300ms 指数退避到 3s，总超时见 <see cref="TransferTimeout"/>。
        /// 完成后返回转存落地的顶层 fid 列表（用于创建分享）。
        /// </summary>
        public async Task<QuarkTaskResult> WaitTaskAsync(string taskId, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.Add(TransferTimeout);
            int delayMs = 300;
            int retryIndex = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string query = DefaultQuery
                    + "&task_id=" + Uri.EscapeDataString(taskId)
                    + "&retry_index=" + retryIndex;
                JObject data = await GetAsync("task", query, token).ConfigureAwait(false);
                int status = data.Value<int?>("status") ?? -1;
                // 0 等待中 1 进行中 2 已完成 3 失败
                if (status == 2)
                {
                    return new QuarkTaskResult
                    {
                        TopFids = ReadTopFids(data),
                        Message = data.Value<string>("message") ?? string.Empty,
                    };
                }
                if (status != 0 && status != 1)
                {
                    string message = data.Value<string>("message") ?? string.Empty;
                    if (message.Length == 0)
                    {
                        message = "转存任务失败（状态 " + status + "）";
                    }
                    throw new QuarkPanException(0, message);
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new QuarkPanException(0, string.Format(
                        "转存任务超时（超过 {0} 分钟仍在处理，可稍后到网盘确认）",
                        (int)TransferTimeout.TotalMinutes));
                }

                await Task.Delay(delayMs, token).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 3000);
                retryIndex++;
            }
        }

        private static List<string> ReadTopFids(JObject data)
        {
            List<string> result = new List<string>();
            JObject saveAs = data["save_as"] as JObject;
            JArray topFids = saveAs == null ? null : saveAs["save_as_top_fids"] as JArray;
            if (topFids == null)
            {
                return result;
            }
            foreach (JToken item in topFids)
            {
                string fid = item.Value<string>() ?? string.Empty;
                if (fid.Length > 0)
                {
                    result.Add(fid);
                }
            }
            return result;
        }

        /// <summary>
        /// 为指定文件/目录创建永久、无提取码的分享，返回分享链接。
        /// 注意：夸克该接口是异步任务，必须传 expired_type（1=永久），否则返回 41000「未知过期类型」；
        /// 提交成功只返回 task_id，需轮询 task 取 share_id。
        /// </summary>
        public async Task<string> CreateShareAsync(string fid, string title, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(fid))
            {
                throw new QuarkPanException(0, "缺少转存后的文件 ID，无法创建分享");
            }
            JObject body = new JObject
            {
                ["fid_list"] = new JArray(fid),
                ["title"] = NormalizeShareTitle(title),
                ["url_type"] = 1,
                ["expired_type"] = 1,
            };
            JObject data = await PostAsync("share", DefaultQuery, body, token).ConfigureAwait(false);
            string shareId = data.Value<string>("share_id") ?? string.Empty;
            if (shareId.Length == 0)
            {
                string taskId = data.Value<string>("task_id") ?? string.Empty;
                if (taskId.Length > 0)
                {
                    JObject taskData = await WaitShareTaskAsync(taskId, token).ConfigureAwait(false);
                    shareId = taskData.Value<string>("share_id") ?? string.Empty;
                }
            }
            if (shareId.Length == 0)
            {
                throw new QuarkPanException(0, "创建分享失败（未返回 share_id）");
            }
            return await ResolveShareUrlAsync(shareId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 把分享任务返回的内部 share_id（32 位 UUID）换成对外可访问的短链。
        /// 说明：任务里的 share_id 不是链接 ID，直接用它会得到无效链接；
        /// 网页端创建分享后也是调用 share/password 拿 share_url / pwd_id。
        /// </summary>
        public async Task<string> ResolveShareUrlAsync(string shareId, CancellationToken token)
        {
            try
            {
                JObject body = new JObject
                {
                    ["share_id"] = shareId,
                };
                JObject data = await PostAsync("share/password", DefaultQuery, body, token).ConfigureAwait(false);
                string shareUrl = data.Value<string>("share_url") ?? string.Empty;
                if (shareUrl.Length > 0)
                {
                    return shareUrl;
                }
                string pwdId = data.Value<string>("pwd_id") ?? string.Empty;
                if (pwdId.Length > 0)
                {
                    return "https://pan.quark.cn/s/" + pwdId;
                }
            }
            catch (QuarkPanException)
            {
                // 查询短链失败时退回内部 share_id 拼装，避免整条记录失败
            }
            return "https://pan.quark.cn/s/" + shareId;
        }

        /// <summary>分享标题规范化：压缩换行/空白，并按服务端长度限制截断（过长会创建失败）。</summary>
        private static string NormalizeShareTitle(string title)
        {
            string text = Regex.Replace(title ?? string.Empty, @"\s+", " ").Trim();
            if (text.Length == 0)
            {
                return "资源分享";
            }
            const int MaxLength = 50;
            return text.Length <= MaxLength ? text : text.Substring(0, MaxLength);
        }

        /// <summary>
        /// 校验分享链接是否真正可用：个别情况下服务端任务成功但分享未生效（审核中/被拦截）。
        /// 返回空字符串表示通过，否则返回失败原因。
        /// </summary>
        public async Task<string> VerifyShareAsync(string shareUrl, CancellationToken token)
        {
            string shareId = ExtractShareId(shareUrl);
            if (shareId.Length == 0)
            {
                return "分享链接格式不正确";
            }
            try
            {
                JObject body = new JObject
                {
                    ["pwd_id"] = shareId,
                    ["passcode"] = string.Empty,
                };
                JObject data = await PostAsync("share/sharepage/token", DefaultQuery, body, token).ConfigureAwait(false);
                string stoken = data.Value<string>("stoken") ?? string.Empty;
                return stoken.Length > 0 ? string.Empty : "分享未返回访问令牌";
            }
            catch (QuarkPanException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>从分享链接（https://pan.quark.cn/s/xxxx）中取出分享 ID。</summary>
        private static string ExtractShareId(string shareUrl)
        {
            string text = (shareUrl ?? string.Empty).Trim();
            int queryIndex = text.IndexOf('?');
            if (queryIndex >= 0)
            {
                text = text.Substring(0, queryIndex);
            }
            text = text.TrimEnd('/');
            int slashIndex = text.LastIndexOf('/');
            return slashIndex >= 0 ? text.Substring(slashIndex + 1) : string.Empty;
        }

        /// <summary>轮询创建分享任务，完成后返回任务数据（含 share_id）。</summary>
        private async Task<JObject> WaitShareTaskAsync(string taskId, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.Add(ShareTaskTimeout);
            int delayMs = 200;
            int retryIndex = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string query = DefaultQuery
                    + "&task_id=" + Uri.EscapeDataString(taskId)
                    + "&retry_index=" + retryIndex;
                JObject data = await GetAsync("task", query, token).ConfigureAwait(false);
                int status = data.Value<int?>("status") ?? -1;
                if (status == 2)
                {
                    return data;
                }
                if (status != 0 && status != 1)
                {
                    string message = data.Value<string>("message") ?? string.Empty;
                    throw new QuarkPanException(0, message.Length > 0
                        ? "创建分享失败：" + message
                        : "创建分享失败（任务状态 " + status + "）");
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new QuarkPanException(0, "创建分享超时，请稍后到夸克网盘「我的分享」确认");
                }
                await Task.Delay(delayMs, token).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 2000);
                retryIndex++;
            }
        }

        /// <summary>把响应体压缩成一行并截断，便于放进错误提示定位问题。</summary>
        private static string CompressForMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "<空响应>";
            }
            string one = Regex.Replace(text, @"\s+", " ").Trim();
            return one.Length <= 200 ? one : one.Substring(0, 200) + "...";
        }

        private Task<JObject> GetAsync(string path, string query, CancellationToken token)
        {
            return SendAsync(HttpMethod.Get, path, query, null, token);
        }

        private Task<JObject> PostAsync(string path, string query, JObject body, CancellationToken token)
        {
            return SendAsync(HttpMethod.Post, path, query, body, token);
        }

        /// <summary>
        /// 发送请求并解析夸克统一响应体 {status, code, message, data}；
        /// 业务码非 0 时抛出 <see cref="QuarkPanException"/>，登录态失效单独标记。
        /// </summary>
        private async Task<JObject> SendAsync(
            HttpMethod method,
            string path,
            string query,
            JObject body,
            CancellationToken token)
        {
            if (!HasCookie)
            {
                throw new QuarkPanException(0, "未配置夸克 Cookie，请先在「网盘Cookie设置」中填写", true);
            }

            string url = BaseUrl + "/" + path.TrimStart('/') + "?" + (query ?? string.Empty);
            using HttpRequestMessage request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (body != null)
            {
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new QuarkPanException(0, "请求夸克接口超时，请稍后重试");
            }
            catch (HttpRequestException ex)
            {
                throw new QuarkPanException(0, "请求夸克接口失败：" + ex.Message);
            }

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (statusCode == 401 || statusCode == 403)
                {
                    throw new QuarkPanException(0, "夸克登录态已失效（HTTP " + statusCode + "），请更新 Cookie", true);
                }

                JObject root;
                try
                {
                    root = JObject.Parse(text);
                }
                catch (Exception)
                {
                    string snippet = CompressForMessage(text);
                    if (statusCode == 400)
                    {
                        throw new QuarkPanException(0,
                            "夸克返回 HTTP 400：请求被拒绝，通常是 Cookie 不完整或含换行/空格，"
                            + "请重新复制一行完整 Cookie（需含 __puus）后重试。响应片段：" + snippet);
                    }
                    throw new QuarkPanException(0, "夸克接口返回异常内容（HTTP " + statusCode + "），响应片段：" + snippet);
                }

                int code = root.Value<int?>("code") ?? 0;
                string message = root.Value<string>("message") ?? string.Empty;
                if (code != 0)
                {
                    string detail = message.Length > 0 ? message : "错误码 " + code;
                    bool cookieInvalid = code == QuarkPanException.CodeCookieInvalid
                        || message.Contains("登录", StringComparison.Ordinal);
                    throw new QuarkPanException(code, cookieInvalid ? "夸克 Cookie 已失效，请重新配置：" + detail : detail, cookieInvalid);
                }

                return root["data"] as JObject ?? new JObject();
            }
        }
    }
}
