using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage.Quark
{
    /// <summary>
    /// 描 述：夸克分享转存任务执行器。
    /// 流程：读 quark_cookie → 校验登录态 → 确保「夸克默认转存目录」一级目录 → 逐条按资源分类解析子目录，
    /// 再解析/判重/转存/创建永久分享 → 写入 pan_source。
    /// 单条失败不影响后续；Cookie 失效则中止整批（已完成条目的落库结果保留）。
    /// </summary>
    public sealed class QuarkPanTransferRunner
    {
        /// <summary>「夸克默认转存目录」配置项 key。</summary>
        private const string TransferFolderConfKey = "quark_transfer_dir";

        /// <summary>配置项为空时的一级目录回退值。</summary>
        private const string DefaultTargetFolderName = "采集资源";

        private const int ItemDelayMs = 400;
        private const int MaxTitleLength = 255;
        private const int MaxDescriptionLength = 255;
        private const int MaxContentLength = 1000;
        private const int MaxTextLength = 2000;
        private const int MaxFolderNameLength = 100;

        private readonly PanSourceCollectService panSourceCollectService = new PanSourceCollectService();
        private readonly PanSourceService panSourceService = new PanSourceService();
        private readonly PanSourceCategoryBLL panSourceCategoryBLL = new PanSourceCategoryBLL();
        private readonly PanConfBLL panConfBLL = new PanConfBLL();

        /// <summary>在后台线程启动转存任务（不阻塞请求）。</summary>
        public static void Start(string jobId, CancellationToken token)
        {
            QuarkPanTransferRunner runner = new QuarkPanTransferRunner();
            _ = Task.Run(() => runner.RunAsync(jobId, token), CancellationToken.None);
        }

        private async Task RunAsync(string jobId, CancellationToken token)
        {
            try
            {
                QuarkPanTransferState state = QuarkPanTransferJobManager.Get(jobId);
                if (state == null || state.Items.Count == 0)
                {
                    FinishFailed(jobId, "转存任务不存在或已失效");
                    return;
                }
                List<long> ids = state.Items.Select(t => t.Id).ToList();

                string cookie = await GetCookieAsync();
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    FinishFailed(jobId, "请先在「网盘Cookie设置」中配置夸克 Cookie（需包含 __puus）");
                    return;
                }

                QuarkPanClient client = new QuarkPanClient(cookie);
                // 只记录诊断特征，不写 Cookie 值本身
                LogHelper.Info(string.Format(
                    "夸克转存开始：Cookie 长度 {0}，含 __puus={1}，含空白字符={2}",
                    cookie.Length,
                    cookie.Contains("__puus", StringComparison.OrdinalIgnoreCase),
                    cookie.Any(char.IsWhiteSpace)));
                string accountInfo;
                try
                {
                    accountInfo = await client.CheckCookieAsync(token);
                }
                catch (QuarkPanException ex) when (ex.CookieInvalid)
                {
                    FinishFailed(jobId, "夸克 Cookie 已失效，请重新配置：" + ex.Message);
                    return;
                }
                catch (QuarkPanException ex)
                {
                    LogHelper.Error(ex);
                    FinishFailed(jobId, "夸克账号校验失败：" + ex.Message);
                    return;
                }

                string targetFolderName = SanitizeFolderName(await GetTargetFolderNameAsync());
                if (targetFolderName.Length == 0)
                {
                    targetFolderName = DefaultTargetFolderName;
                }
                UpdateMessage(jobId, "登录态正常（" + accountInfo + "），正在准备目录「" + targetFolderName + "」");

                string parentFid;
                try
                {
                    parentFid = await client.EnsureTopFolderAsync(targetFolderName, token);
                }
                catch (QuarkPanException ex) when (ex.CookieInvalid)
                {
                    FinishFailed(jobId, "夸克 Cookie 已失效，请重新配置：" + ex.Message);
                    return;
                }
                catch (QuarkPanException ex)
                {
                    FinishFailed(jobId, "准备网盘目录「" + targetFolderName + "」失败：" + ex.Message);
                    return;
                }

                List<PanSourceCollectEntity> records = await panSourceCollectService.GetListByIds(ids);
                Dictionary<long, PanSourceCollectEntity> recordMap = new Dictionary<long, PanSourceCollectEntity>();
                foreach (PanSourceCollectEntity record in records)
                {
                    if (record.Id != null)
                    {
                        recordMap[record.Id.Value] = record;
                    }
                }
                // 分类名称一次取全量后内存映射，避免逐条查库
                Dictionary<long, string> categoryNameMap = await GetCategoryNameMapAsync();
                // (父目录 fid + 分类名) → 子目录 fid，同一批内只解析一次
                Dictionary<string, string> folderFidCache = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (long id in ids)
                {
                    token.ThrowIfCancellationRequested();

                    PanSourceCollectEntity record;
                    if (!recordMap.TryGetValue(id, out record))
                    {
                        QuarkPanTransferJobManager.UpdateItem(
                            jobId, id, QuarkPanTransferItem.StatusFailed, "采集记录不存在，可能已被删除", string.Empty);
                        continue;
                    }

                    string status = QuarkPanTransferItem.StatusFailed;
                    string message = string.Empty;
                    string shareUrl = string.Empty;
                    try
                    {
                        string categoryName = GetCategoryName(record, categoryNameMap);
                        TransferOutcome outcome = await TransferOneAsync(
                            client, record, parentFid, targetFolderName, categoryName, folderFidCache, token);
                        status = outcome.Status;
                        message = outcome.Message;
                        shareUrl = outcome.ShareUrl;
                    }
                    catch (QuarkPanException ex) when (ex.CookieInvalid)
                    {
                        throw;
                    }
                    catch (QuarkPanException ex)
                    {
                        status = QuarkPanTransferItem.StatusFailed;
                        message = ex.Message;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error(ex);
                        status = QuarkPanTransferItem.StatusFailed;
                        message = "转存异常：" + ex.Message;
                    }

                    QuarkPanTransferJobManager.UpdateItem(jobId, id, status, message, shareUrl);
                    await Task.Delay(ItemDelayMs, token).ConfigureAwait(false);
                }

                QuarkPanTransferState final = QuarkPanTransferJobManager.Get(jobId);
                string summary = final == null
                    ? "转存完成"
                    : string.Format("转存完成：共 {0} 条，成功 {1} 条，跳过 {2} 条，失败 {3} 条",
                        final.Total, final.Succeeded, final.Skipped, final.Failed);
                QuarkPanTransferJobManager.Finish(jobId, QuarkPanTransferJobManager.StatusSucceeded, summary);
            }
            catch (OperationCanceledException)
            {
                QuarkPanTransferJobManager.Finish(
                    jobId, QuarkPanTransferJobManager.StatusCanceled, "转存已取消，已完成条目的本地资源保留");
            }
            catch (QuarkPanException ex) when (ex.CookieInvalid)
            {
                FinishFailed(jobId, "夸克 Cookie 已失效，转存已中止：" + ex.Message);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex);
                FinishFailed(jobId, "转存失败：" + ex.Message);
            }
        }

        /// <summary>单条资源的处理结果。</summary>
        private sealed class TransferOutcome
        {
            public string Status { get; set; } = QuarkPanTransferItem.StatusFailed;

            public string Message { get; set; } = string.Empty;

            public string ShareUrl { get; set; } = string.Empty;
        }

        /// <summary>
        /// 处理单条采集记录：解析链接 → 判重 → 转存到目标目录 → 等待任务 → 创建永久分享 → 写入本地资源。
        /// 非夸克链接、已转存过、空分享等按「跳过」返回，不视为失败。
        /// </summary>
        private async Task<TransferOutcome> TransferOneAsync(
            QuarkPanClient client,
            PanSourceCollectEntity record,
            string parentFid,
            string targetFolderName,
            string categoryName,
            Dictionary<string, string> folderFidCache,
            CancellationToken token)
        {
            if (!QuarkShareLink.TryParse(record.ResourceUrl, out QuarkShareLink link, out string parseError))
            {
                return new TransferOutcome
                {
                    Status = QuarkPanTransferItem.StatusSkipped,
                    Message = parseError,
                };
            }

            PanSourceEntity exist = await panSourceService.GetByOriginalUrl(link.RawUrl);
            if (exist != null)
            {
                string fixedUrl = await TryFixShareUrlAsync(client, exist, token);
                return new TransferOutcome
                {
                    Status = QuarkPanTransferItem.StatusSkipped,
                    Message = fixedUrl.Length == 0
                        ? "本地资源已存在同一链接，跳过"
                        : "本地资源已存在，已把分享地址修正为：" + fixedUrl,
                };
            }

            QuarkShareToken shareToken = await client.GetShareTokenAsync(link, token);
            List<QuarkShareFile> files = await client.GetShareFilesAsync(link, shareToken.Stoken, token);
            if (files.Count == 0)
            {
                return new TransferOutcome
                {
                    Status = QuarkPanTransferItem.StatusSkipped,
                    Message = "分享内没有可转存的文件（可能已失效）",
                };
            }

            // 目标目录：一级为配置的默认转存目录，二级为资源分类（分类为空时直接存一级目录）
            string folderFid = await ResolveTargetFolderFidAsync(
                client, parentFid, categoryName, folderFidCache, token);

            string taskId = await client.SaveAsync(link, shareToken.Stoken, folderFid, files, token);
            QuarkTaskResult taskResult = await client.WaitTaskAsync(taskId, token);
            string fid = taskResult.TopFids.FirstOrDefault() ?? string.Empty;
            if (fid.Length == 0)
            {
                return new TransferOutcome
                {
                    Status = QuarkPanTransferItem.StatusFailed,
                    Message = "转存已完成但未返回文件 ID，无法创建分享（文件可能已在网盘中）",
                };
            }

            string title = ResolveTitle(record, shareToken, files);
            string shareUrl = await client.CreateShareAsync(fid, title, token);
            // 自检：服务端偶发"任务成功但分享未生效"，避免把不可用链接写进本地资源
            string shareWarning = await client.VerifyShareAsync(shareUrl, token);

            PanSourceEntity entity = BuildSourceEntity(record, title, shareUrl, link.RawUrl, fid);
            await panSourceService.SaveTransfer(entity);

            string folderPath = categoryName.Length == 0
                ? targetFolderName
                : targetFolderName + "/" + categoryName;
            return new TransferOutcome
            {
                Status = QuarkPanTransferItem.StatusSucceeded,
                Message = shareWarning.Length == 0
                    ? "转存并分享成功（已存入「" + folderPath + "」）：" + shareUrl
                    : "转存并分享成功（已存入「" + folderPath + "」）：" + shareUrl
                        + "（提示：分享自检未通过 - " + shareWarning + "，请到夸克「我的分享」确认）",
                ShareUrl = shareUrl,
            };
        }

        /// <summary>
        /// 修复历史数据：早期版本把分享任务返回的内部 share_id（32 位 UUID）当成了分享地址，
        /// 这里用内部 ID 重新换取真正的短链并回写。非该情形返回空字符串。
        /// </summary>
        private async Task<string> TryFixShareUrlAsync(QuarkPanClient client, PanSourceEntity entity, CancellationToken token)
        {
            string url = (entity.Url ?? string.Empty).Trim();
            Match match = Regex.Match(url, @"^https?://pan\.quark\.cn/s/([0-9a-fA-F]{32})$");
            if (!match.Success)
            {
                return string.Empty;
            }
            string shareId = match.Groups[1].Value;
            try
            {
                string fixedUrl = await client.ResolveShareUrlAsync(shareId, token);
                // 换不到短链时会原样返回内部 ID，此时不更新，避免写入同样无效的地址
                if (fixedUrl.EndsWith(shareId, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }
                await panSourceService.UpdateUrl(entity.Id ?? 0, fixedUrl);
                return fixedUrl;
            }
            catch (QuarkPanException)
            {
                return string.Empty;
            }
        }

        /// <summary>读取 quark_cookie 配置值（每次任务启动时读库，支持页面随时更换）。</summary>
        private async Task<string> GetCookieAsync()
        {
            var conf = await panConfBLL.GetConfValues(new List<string> { "quark_cookie" });
            if (conf == null || conf.Data == null || !conf.Data.TryGetValue("quark_cookie", out string cookie))
            {
                return string.Empty;
            }
            return (cookie ?? string.Empty).Trim();
        }

        /// <summary>读取「夸克默认转存目录」配置值；未配置或为空时回退为「采集资源」。</summary>
        private async Task<string> GetTargetFolderNameAsync()
        {
            var conf = await panConfBLL.GetConfValues(new List<string> { TransferFolderConfKey });
            string name = string.Empty;
            if (conf != null && conf.Data != null)
            {
                conf.Data.TryGetValue(TransferFolderConfKey, out name);
            }
            name = (name ?? string.Empty).Trim();
            return name.Length == 0 ? DefaultTargetFolderName : name;
        }

        /// <summary>一次性读取全部分类，构建 分类Id → 分类名称 映射（表数据量小）。</summary>
        private async Task<Dictionary<long, string>> GetCategoryNameMapAsync()
        {
            Dictionary<long, string> map = new Dictionary<long, string>();
            try
            {
                TData<List<PanSourceCategoryEntity>> result =
                    await panSourceCategoryBLL.GetList(new PanSourceCategoryListParam());
                if (result == null || result.Data == null)
                {
                    return map;
                }
                foreach (PanSourceCategoryEntity item in result.Data)
                {
                    if (item.Id != null && item.Id.Value > 0)
                    {
                        map[item.Id.Value] = (item.Name ?? string.Empty).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                // 分类读取失败不影响转存主流程，全部按无分类处理
                LogHelper.Error(ex);
            }
            return map;
        }

        /// <summary>取资源所属分类名称；未分类或分类已被删除时返回空字符串。</summary>
        private static string GetCategoryName(PanSourceCollectEntity record, Dictionary<long, string> categoryNameMap)
        {
            if (record.SourceCategoryId == null || record.SourceCategoryId.Value <= 0)
            {
                return string.Empty;
            }
            return categoryNameMap.TryGetValue(record.SourceCategoryId.Value, out string name)
                ? (name ?? string.Empty).Trim()
                : string.Empty;
        }

        /// <summary>
        /// 解析本条资源的转存目录 fid：分类名有效时取（或创建）一级目录下的分类子目录，否则直接用一级目录。
        /// 同一批任务内按「父 fid + 分类名」缓存，避免重复请求网盘。
        /// </summary>
        private static async Task<string> ResolveTargetFolderFidAsync(
            QuarkPanClient client,
            string parentFid,
            string categoryName,
            Dictionary<string, string> folderFidCache,
            CancellationToken token)
        {
            string folderName = SanitizeFolderName(categoryName);
            if (folderName.Length == 0 || string.IsNullOrWhiteSpace(parentFid))
            {
                return parentFid;
            }
            string cacheKey = parentFid + "|" + folderName;
            if (folderFidCache.TryGetValue(cacheKey, out string cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }
            string fid = await client.EnsureChildFolderAsync(parentFid, folderName, token);
            folderFidCache[cacheKey] = fid;
            return fid;
        }

        /// <summary>
        /// 净化网盘文件夹名称：去掉首尾空白与点、把 \/:*?"&lt;&gt;| 替换为下划线并限长，
        /// 避免非法名称导致建目录接口报错。清理后为空表示不使用该级目录。
        /// </summary>
        private static string SanitizeFolderName(string name)
        {
            string text = (name ?? string.Empty).Trim().Trim('.');
            char[] invalidChars = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            foreach (char c in invalidChars)
            {
                if (text.IndexOf(c) >= 0)
                {
                    text = text.Replace(c, '_');
                }
            }
            text = text.Trim().Trim('.');
            if (text.Length > MaxFolderNameLength)
            {
                text = text.Substring(0, MaxFolderNameLength).Trim().Trim('.');
            }
            return text;
        }

        /// <summary>标题优先用采集到的资源名称，其次分享标题，最后用文件名。</summary>
        private static string ResolveTitle(
            PanSourceCollectEntity record,
            QuarkShareToken shareToken,
            List<QuarkShareFile> files)
        {
            string title = Truncate(record.ResourceName, MaxTitleLength);
            if (title.Length == 0)
            {
                title = Truncate(shareToken.Title, MaxTitleLength);
            }
            if (title.Length == 0 && files.Count > 0)
            {
                title = Truncate(files[0].FileName, MaxTitleLength);
            }
            return title.Length == 0 ? "资源分享" : title;
        }

        /// <summary>组装写入 pan_source 的实体：content 存原始分享链接用于判重，code 留空表示无提取码。</summary>
        private static PanSourceEntity BuildSourceEntity(
            PanSourceCollectEntity record,
            string title,
            string shareUrl,
            string originalUrl,
            string fid)
        {
            return new PanSourceEntity
            {
                Title = Truncate(title, MaxTitleLength),
                Url = shareUrl,
                Code = string.Empty,
                Content = Truncate(originalUrl, MaxContentLength),
                Description = Truncate(record.ResourceIntro, MaxDescriptionLength),
                VodContent = Truncate(record.ResourceIntro, MaxTextLength),
                VodPic = ResolveVodPic(record),
                Fid = fid,
                IsType = 0,
                IsUser = 0,
                Status = 1,
                IsDelete = 0,
                IsTop = 0,
                IsTime = 0,
                PageViews = 0,
                Sort = 0,
            };
        }

        /// <summary>
        /// 前台封面图：优先使用已下载到本机的图片，补图未完成时回退采集到的原图地址；
        /// 多图串超长时整条丢弃，避免前台轮播出现半截地址。
        /// </summary>
        private static string ResolveVodPic(PanSourceCollectEntity record)
        {
            string images = string.IsNullOrWhiteSpace(record.ImageUrl) ? record.CollectImageUrl : record.ImageUrl;
            if (string.IsNullOrWhiteSpace(images))
            {
                return string.Empty;
            }
            string value = images.Trim();
            return value.Length <= MaxContentLength ? value : string.Empty;
        }

        private static void FinishFailed(string jobId, string message)
        {
            QuarkPanTransferJobManager.Finish(jobId, QuarkPanTransferJobManager.StatusFailed, message);
        }

        private static void UpdateMessage(string jobId, string message)
        {
            QuarkPanTransferJobManager.Update(jobId, state => state.Message = message);
        }

        /// <summary>按数据库字段长度截断（varchar 按字符数计）。</summary>
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
