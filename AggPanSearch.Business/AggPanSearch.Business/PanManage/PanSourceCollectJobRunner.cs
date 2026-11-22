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
            try
            {
                SiteCollectResult result = await new SiteCollectService().CollectAsync(
                    target.TargetUrl,
                    target.PageTemplate,
                    target.Mode,
                    async (page, ct) =>
                    {
                        (int inserted, int duplicated) = await PersistBatchAsync(page.Items, collectSource, page.PageUrl, category, categoryMap);
                        PanSourceCollectJobManager.Update(jobId, state =>
                        {
                            state.Inserted += inserted;
                            state.Duplicated += duplicated;
                        });
                    },
                    progress =>
                    {
                        PanSourceCollectJobManager.Update(jobId, state =>
                        {
                            state.PagesDone = progress.ListPagesDone;
                            state.PagesTotal = progress.ListPagesTotal;
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
                        : string.Format("采集完成：处理 {0} 页，发现 {1} 条，新增 {2} 条，重复跳过 {3} 条，无效跳过 {4} 条{5}",
                            state.PagesDone, state.Found, state.Inserted, state.Duplicated, state.Skipped,
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
        }

        /// <summary>
        /// 把一页采集结果按资源链接去重后写入数据库，返回新增与重复的条数。
        /// 分类字段写「分类资源」Id：category 非空时用它查找映射，否则用解析器识别出的分类名；
        /// 两者都映射不到时写 NULL（未分类），不再保存源站文本分类。
        /// </summary>
        private async Task<(int Inserted, int Duplicated)> PersistBatchAsync(
            List<CollectedItem> items, string collectSource, string pageUrl, string category,
            PanSourceCollectCategoryMap categoryMap)
        {
            List<CollectedItem> valid = items
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Url))
                .ToList();
            if (valid.Count == 0)
            {
                return (0, 0);
            }

            List<string> urls = valid
                .Select(t => t.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            HashSet<string> existingUrls = await panSourceCollectService.GetExistingUrls(urls);

            List<PanSourceCollectEntity> entities = new List<PanSourceCollectEntity>();
            HashSet<string> batchUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicated = 0;
            foreach (CollectedItem item in valid)
            {
                if (existingUrls.Contains(item.Url) || !batchUrls.Add(item.Url))
                {
                    duplicated++;
                    continue;
                }
                string siteCategoryName = string.IsNullOrWhiteSpace(category) ? item.Category : category;
                // 原图片：多图按条累加拼接，超长整条丢弃；本地图片由补图任务稍后回写
                string collectImageUrl = SiteCollectHtmlUtil.JoinImages(
                    item.ImageUrls.Count > 0 ? item.ImageUrls : SiteCollectHtmlUtil.SplitImages(item.ImageUrl),
                    MaxImageUrlLength);
                entities.Add(new PanSourceCollectEntity
                {
                    CollectSource = Truncate(collectSource, MaxSourceLength),
                    CollectSourceUrl = Truncate(item.DetailUrl.Length > 0 ? item.DetailUrl : pageUrl, MaxSourceUrlLength),
                    SourceCategoryId = categoryMap.Resolve(siteCategoryName, pageUrl),
                    ResourceName = Truncate(item.Name, MaxResourceNameLength),
                    ResourceIntro = Truncate(SiteCollectIntroCleaner.Clean(item.Intro), MaxIntroLength),
                    ResourceUrl = Truncate(item.Url, MaxResourceUrlLength),
                    CollectImageUrl = collectImageUrl,
                    ImageUrl = string.Empty,
                    IsLinkValid = 1,
                });
            }

            if (entities.Count > 0)
            {
                await panSourceCollectService.InsertBatch(entities);
                // 入库后把新增记录交给补图任务异步下载本地图片，不阻塞采集主流程
                List<long> newIds = entities
                    .Where(t => t.Id != null && t.Id.Value > 0)
                    .Select(t => t.Id.Value)
                    .ToList();
                if (newIds.Count > 0)
                {
                    PanSourceCollectImageJobRunner.Enqueue(newIds);
                }
            }
            return (entities.Count, duplicated);
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
