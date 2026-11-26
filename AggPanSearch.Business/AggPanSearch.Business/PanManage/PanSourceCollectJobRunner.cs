using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集任务执行器。
    /// 负责调用采集引擎、把进度回写到任务注册表、逐页把结果去重入库，并做成功/失败/取消的收尾。
    /// </summary>
    public sealed class PanSourceCollectJobRunner
    {
        private const int MaxSourceLength = 100;
        private const int MaxSourceUrlLength = 1000;
        private const int MaxResourceNameLength = 500;
        private const int MaxIntroLength = 2000;
        private const int MaxResourceUrlLength = 1000;
        private const int MaxImageUrlLength = 1000;

        private readonly PanSourceCollectService panSourceCollectService = new PanSourceCollectService();

        /// <summary>本次任务已识别过的资源链接（升为任务级去重集合，替代原页内集合，跨页与页内重复统一兜底）。</summary>
        private readonly HashSet<string> seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>在后台线程启动采集任务（不阻塞请求）。</summary>
        public static void Start(
            string jobId,
            PanSourceCollectTarget target,
            string siteName,
            string category,
            PanSourceCollectCategoryMap categoryMap,
            int maxPages,
            CancellationToken token)
        {
            PanSourceCollectJobRunner runner = new PanSourceCollectJobRunner();
            _ = Task.Run(
                () => runner.RunAsync(jobId, target, siteName, category, categoryMap, maxPages, token),
                CancellationToken.None);
        }

        private async Task RunAsync(
            string jobId,
            PanSourceCollectTarget target,
            string collectSource,
            string category,
            PanSourceCollectCategoryMap categoryMap,
            int maxPages,
            CancellationToken token)
        {
            // 本次任务所有新增入库记录的 Id，采集全部结束后统一交给资源图片下载队列
            List<long> insertedIds = new List<long>();
            // 本次任务新增记录的图片总张数（按下载端同口径先精确算出，交队列作为 Total）
            int collectedImages = 0;
            try
            {
                SiteCollectResult result = await new SiteCollectService().CollectAsync(
                    target.TargetUrl,
                    target.PageTemplate,
                    target.Mode,
                    async (page, ct) =>
                    {
                        // 每页一次性批量查询库内已有链接做快照去重，避免逐条 SELECT 的 N+1 开销；
                        // 本条是否已入库的最终判定由任务级 seenUrls + 库内快照共同完成。
                        List<string> urls = page.Items
                            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Url))
                            .Select(t => t.Url)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        HashSet<string> existing = urls.Count > 0
                            ? await panSourceCollectService.GetExistingUrls(urls)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // 以条为批次：采集一条入库一条，逐条刷新进度
                        foreach (CollectedItem item in page.Items)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Url))
                            {
                                continue;
                            }
                            if (existing.Contains(item.Url) || !seenUrls.Add(item.Url))
                            {
                                PanSourceCollectJobManager.Update(jobId, state => state.Duplicated++);
                                continue;
                            }

                            PanSourceCollectEntity entity = BuildEntity(item, collectSource, page.PageUrl, category, categoryMap);
                            await panSourceCollectService.InsertBatch(new List<PanSourceCollectEntity> { entity });

                            // 先只入库收集 Id，全部采集结束后统一交给资源图片下载队列
                            if (entity.Id != null && entity.Id.Value > 0)
                            {
                                insertedIds.Add(entity.Id.Value);
                                collectedImages += CountImages(entity.CollectImageUrl);
                            }

                            PanSourceCollectJobManager.Update(jobId, state => state.Inserted++);
                        }
                    },
                    progress =>
                    {
                        PanSourceCollectJobManager.Update(jobId, state =>
                        {
                            state.TotalFound = progress.TotalFound;
                            state.Found = progress.Found;
                            state.Skipped = progress.Skipped;
                            if (!string.IsNullOrWhiteSpace(progress.Message))
                            {
                                state.Message = progress.Message;
                            }
                        });
                    },
                    token,
                    maxPages,
                    target.StartPage);

                PanSourceCollectJobState state = PanSourceCollectJobManager.Get(jobId);
                if (result.Success)
                {
                    string message = state == null
                        ? result.Message
                        : string.Format("采集完成：发现 {0} 条，新增 {1} 条，重复跳过 {2} 条，无效跳过 {3} 条{4}",
                            state.Found, state.Inserted, state.Duplicated, state.Skipped,
                            state.LimitReached ? "（已达单次采集上限）" : string.Empty);
                    PanSourceCollectJobManager.Finish(
                        jobId, PanSourceCollectJobManager.StatusSucceeded, message, result.LimitReached);
                }
                else
                {
                    PanSourceCollectJobManager.Finish(
                        jobId, PanSourceCollectJobManager.StatusFailed, result.Message, result.LimitReached);
                }
            }
            catch (OperationCanceledException)
            {
                PanSourceCollectJobManager.Finish(
                    jobId, PanSourceCollectJobManager.StatusCanceled, "采集已取消，已入库的数据保留", false);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex);
                PanSourceCollectJobManager.Finish(
                    jobId, PanSourceCollectJobManager.StatusFailed, "采集失败：" + ex.Message, false);
            }
            finally
            {
                // 全部采集入库完成后再统一交给资源图片下载队列，不与采集流程抢占资源；
                // 同时传入已精确算出的图片总张数，作为进度 Total
                if (insertedIds.Count > 0)
                {
                    PanSourceCollectImageJobRunner.Enqueue(insertedIds, collectedImages);
                }
            }
        }

        /// <summary>
        /// 把单条采集条目按数据库字段长度规则裁剪为入库实体。
        /// 分类字段写「分类资源」Id：category 非空时用它查找映射，否则用解析器识别出的分类名；
        /// 两者都映射不到时写 NULL（未分类），不再保存源站文本分类。
        /// </summary>
        private static PanSourceCollectEntity BuildEntity(
            CollectedItem item, string collectSource, string pageUrl, string category,
            PanSourceCollectCategoryMap categoryMap)
        {
            string siteCategoryName = string.IsNullOrWhiteSpace(category) ? item.Category : category;
            // 原图片：多图按条累加拼接，超长整条丢弃；本地图片由资源图片下载队列稍后回写
            string collectImageUrl = SiteCollectHtmlUtil.JoinImages(
                item.ImageUrls.Count > 0 ? item.ImageUrls : SiteCollectHtmlUtil.SplitImages(item.ImageUrl),
                MaxImageUrlLength);
            return new PanSourceCollectEntity
            {
                CollectSource = Truncate(collectSource, MaxSourceLength),
                CollectSourceUrl = Truncate(item.DetailUrl.Length > 0 ? item.DetailUrl : pageUrl, MaxSourceUrlLength),
                SourceCategoryId = categoryMap.Resolve(siteCategoryName, pageUrl),
                ResourceName = Truncate(item.Name, MaxResourceNameLength),
                ResourceIntro = Truncate(SiteCollectIntroCleaner.Clean(item.Intro), MaxIntroLength),
                ResourceUrl = Truncate(item.Url, MaxResourceUrlLength),
                CollectImageUrl = collectImageUrl,
                ImageUrl = string.Empty,
                // 采集入库默认未检测，待资源分析完成后回写 1/0
                IsLinkValid = -1,
            };
        }

        /// <summary>图片张数统计口径，与资源图片下载端完全一致：过滤无效地址、最多取 MaxImageCount 张。</summary>
        private static int CountImages(string collectImageUrl)
        {
            return SiteCollectHtmlUtil.SplitImages(collectImageUrl)
                .Where(image => SiteCollectHtmlUtil.TryCreateHttpUri(image, out _))
                .Take(SiteCollectHtmlUtil.MaxImageCount)
                .Count();
        }

        /// <summary>按数据库字段长度截断（varchar 按字符数计）。</summary>
        private static string Truncate(string? value, int maxLength)
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
