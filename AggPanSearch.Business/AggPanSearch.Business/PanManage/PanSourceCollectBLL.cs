using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using AggPanSearch.Business.PanManage.Quark;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集资源业务类（pan_source_collect）。
    /// 后台「网盘管理 - 采集资源」维护采集到的资源条目、标记链接有效性，
    /// 并负责站点采集任务的启动、进度查询与取消。
    /// 目标解析见 <see cref="PanSourceCollectTargetResolver"/>，任务执行见 <see cref="PanSourceCollectJobRunner"/>。
    /// </summary>
    public class PanSourceCollectBLL
    {
        private const int MaxCustomPages = 100;

        /// <summary>手动补图一次最多处理的记录数。</summary>
        private const int MaxBackfillImageCount = 50;

        /// <summary>单个图片字段的最大字符数（与数据库 varchar(1000) 保持一致）。</summary>
        private const int MaxImageUrlLength = 1000;

        /// <summary>单条资源最多保留的图片数量。</summary>
        private const int MaxImageCount = 5;

        private PanSourceCollectService panSourceCollectService = new PanSourceCollectService();

        private PanSourceCategoryBLL panSourceCategoryBLL = new PanSourceCategoryBLL();

        #region 获取数据

        public async Task<TData<List<PanSourceCollectEntity>>> GetList(PanSourceCollectListParam param)
        {
            TData<List<PanSourceCollectEntity>> obj = new TData<List<PanSourceCollectEntity>>();
            obj.Data = await panSourceCollectService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanSourceCollectEntity>>> GetPageList(PanSourceCollectListParam param, Pagination pagination)
        {
            TData<List<PanSourceCollectEntity>> obj = new TData<List<PanSourceCollectEntity>>();
            obj.Data = await panSourceCollectService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanSourceCollectEntity>> GetEntity(long id)
        {
            TData<PanSourceCollectEntity> obj = new TData<PanSourceCollectEntity>();
            obj.Data = await panSourceCollectService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }

        #endregion

        #region 采集任务

        /// <summary>
        /// 按站点 + 采集模式启动任务（后台执行，前端通过进度接口轮询）。
        /// mode：0 详情页采集 / 1 单个列表页采集 / 2 分页列表采集。
        /// detailUrl：详情页地址，仅模式 0 使用。
        /// listUrl：列表页地址，模式 1 作为入口地址，模式 2 作为起始列表页地址（按其页码开始逐页采集）。
        /// pageTemplate：分页链接模板（含 {page}），仅模式 2 使用。
        /// category：本次采集统一使用的站点分类名（历史兼容参数），留空则按解析器识别出的每一条分类名匹配。
        /// maxPages：本次采集的页数上限，0 表示使用引擎默认值（1-100 之间生效）。
        /// 以上参数均由采集弹窗手动填写，只在本次任务中生效。
        /// </summary>
        public TData<string> StartCollect(
            string siteId,
            int mode,
            string detailUrl = "",
            string category = "",
            int maxPages = 0,
            string listUrl = "",
            string pageTemplate = "")
        {
            TData<string> obj = new TData<string>();
            if (mode < (int)SiteCollectMode.DetailPage || mode > (int)SiteCollectMode.PagedList)
            {
                mode = (int)SiteCollectMode.SingleList;
            }

            PanSourceCollectSite site = PanSourceCollectSiteStore.GetById(siteId);
            if (site == null)
            {
                obj.Message = "站点不存在或已被删除，请刷新页面后重试";
                return obj;
            }

            // 顺序约束：先「配置分类」再「采集」，未配置分类的站点不允许启动任务
            if (site.Categories == null || site.Categories.Count == 0)
            {
                obj.Message = "站点尚未配置分类，请先点击站点卡片上的「配置分类」完成配置";
                return obj;
            }

            if (!PanSourceCollectTargetResolver.TryResolve(
                (SiteCollectMode)mode,
                detailUrl,
                listUrl,
                pageTemplate,
                out PanSourceCollectTarget target,
                out string resolveError))
            {
                obj.Message = resolveError;
                return obj;
            }

            // 站点分类 → 「分类资源」Id 的映射快照：本次任务的分类换算只认这份快照，
            // 任务运行期不再回读 collect-sites.json。
            PanSourceCollectCategoryMap categoryMap = PanSourceCollectCategoryMap.Build(site.Categories);
            string categoryOverride = (category ?? string.Empty).Trim();
            // 0 或非法值走引擎默认上限，1-100 之间按用户输入执行。
            int pageLimit = maxPages <= 0 ? 0 : Math.Min(maxPages, MaxCustomPages);

            string jobId = PanSourceCollectJobManager.Create(
                mode, target.TargetUrl, site.SiteName, site.SiteName, out CancellationToken token, out string createError);
            if (jobId.Length == 0)
            {
                obj.Message = createError;
                return obj;
            }

            PanSourceCollectJobRunner.Start(jobId, target, site.SiteName, categoryOverride, categoryMap, pageLimit, token);

            obj.Data = jobId;
            obj.Tag = 1;
            obj.Message = "采集任务已提交，正在后台采集";
            return obj;
        }

        /// <summary>
        /// 查询采集进度；jobId 为空或已过期时返回最近一个运行中的任务（页面刷新后恢复显示）。
        /// </summary>
        public TData<PanSourceCollectJobState> GetCollectProgress(string jobId)
        {
            TData<PanSourceCollectJobState> obj = new TData<PanSourceCollectJobState>();
            PanSourceCollectJobState state = PanSourceCollectJobManager.Get(jobId);
            if (state == null)
            {
                state = PanSourceCollectJobManager.GetLatestRunning();
            }
            if (state == null)
            {
                obj.Message = "采集任务不存在或已结束";
                return obj;
            }

            obj.Data = state;
            obj.Tag = 1;
            obj.Message = state.Message;
            return obj;
        }

        /// <summary>取消正在运行的采集任务（已入库的数据保留）。</summary>
        public TData CancelCollect(string jobId)
        {
            TData obj = new TData();
            if (!PanSourceCollectJobManager.Cancel(jobId))
            {
                obj.Message = "采集任务不存在或已结束";
                return obj;
            }
            obj.Tag = 1;
            obj.Message = "已提交取消请求，正在停止采集";
            return obj;
        }

        #endregion

        #region 转存并分享（夸克网盘）

        /// <summary>单次最多转存条数，避免一次性占用过长时间与账号风控。</summary>
        private const int MaxTransferCount = 50;

        /// <summary>
        /// 把选中的采集资源转存到本人夸克网盘「采集资源」目录，并生成永久无提取码的分享链接写入本地资源。
        /// 后台执行，前端通过 <see cref="GetTransferProgress"/> 轮询进度。
        /// </summary>
        public async Task<TData<string>> StartTransfer(string ids)
        {
            TData<string> obj = new TData<string>();
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择要转存的资源";
                return obj;
            }

            List<long> idList = idArr.Where(t => t > 0).Distinct().Take(MaxTransferCount).ToList();
            if (idList.Count == 0)
            {
                obj.Message = "请选择要转存的资源";
                return obj;
            }

            List<PanSourceCollectEntity> records = await panSourceCollectService.GetListByIds(idList);
            Dictionary<long, PanSourceCollectEntity> map = records
                .Where(t => t.Id != null)
                .GroupBy(t => t.Id.Value)
                .ToDictionary(g => g.Key, g => g.First());

            List<QuarkPanTransferItem> items = new List<QuarkPanTransferItem>();
            foreach (long id in idList)
            {
                PanSourceCollectEntity record;
                if (!map.TryGetValue(id, out record))
                {
                    continue;
                }
                items.Add(new QuarkPanTransferItem
                {
                    Id = id,
                    Title = Truncate(record.ResourceName, 100),
                    SourceUrl = record.ResourceUrl ?? string.Empty,
                });
            }
            if (items.Count == 0)
            {
                obj.Message = "选中的资源已不存在，请刷新后重试";
                return obj;
            }

            string jobId = QuarkPanTransferJobManager.Create(items, out CancellationToken token, out string createError);
            if (jobId.Length == 0)
            {
                obj.Message = createError;
                return obj;
            }

            QuarkPanTransferRunner.Start(jobId, token);

            obj.Data = jobId;
            obj.Tag = 1;
            obj.Message = "转存任务已提交，正在后台转存并分享";
            return obj;
        }

        /// <summary>
        /// 查询转存进度；jobId 为空或已过期时返回最近一个运行中的任务（页面刷新后恢复显示）。
        /// </summary>
        public TData<QuarkPanTransferState> GetTransferProgress(string jobId)
        {
            TData<QuarkPanTransferState> obj = new TData<QuarkPanTransferState>();
            QuarkPanTransferState state = QuarkPanTransferJobManager.Get(jobId);
            if (state == null)
            {
                state = QuarkPanTransferJobManager.GetLatestRunning();
            }
            if (state == null)
            {
                obj.Message = "转存任务不存在或已结束";
                return obj;
            }

            obj.Data = state;
            obj.Tag = 1;
            obj.Message = state.Message;
            return obj;
        }

        /// <summary>取消正在运行的转存任务（已生成的本地资源保留）。</summary>
        public TData CancelTransfer(string jobId)
        {
            TData obj = new TData();
            if (!QuarkPanTransferJobManager.Cancel(jobId))
            {
                obj.Message = "转存任务不存在或已结束";
                return obj;
            }
            obj.Tag = 1;
            obj.Message = "已提交取消请求，正在停止转存";
            return obj;
        }

        /// <summary>
        /// 启动补图任务：ids 为空时补缺少本地图片的前 limit 条记录。
        /// 原图下载失败会被剔除，任务结束后 collect_image_url 可能变短。
        /// </summary>
        public TData<string> StartBackfillImage(string ids, int limit)
        {
            TData<string> obj = new TData<string>();
            List<long> idList = new List<long>();
            if (!string.IsNullOrWhiteSpace(ids))
            {
                foreach (string id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(id.Trim(), out long value) && value > 0)
                    {
                        idList.Add(value);
                    }
                }
            }
            if (idList.Count == 0)
            {
                limit = limit > 0 ? Math.Min(limit, MaxBackfillImageCount) : MaxBackfillImageCount;
            }

            string jobId = PanSourceCollectImageJobRunner.Start(idList, limit, out string error);
            if (jobId.Length == 0)
            {
                obj.Message = error;
                return obj;
            }
            obj.Data = jobId;
            obj.Tag = 1;
            obj.Message = "补图任务已提交，正在后台下载本地图片";
            return obj;
        }

        /// <summary>查询补图进度；jobId 为空或已过期时返回最近一个运行中的任务。</summary>
        public TData<PanSourceCollectImageJobState> GetImageProgress(string jobId)
        {
            TData<PanSourceCollectImageJobState> obj = new TData<PanSourceCollectImageJobState>();
            PanSourceCollectImageJobState state = PanSourceCollectImageJobManager.Get(jobId);
            if (state == null)
            {
                state = PanSourceCollectImageJobManager.GetLatestRunning();
            }
            if (state == null)
            {
                obj.Message = "补图任务不存在或已结束";
                return obj;
            }
            obj.Data = state;
            obj.Tag = 1;
            obj.Message = state.Message;
            return obj;
        }

        /// <summary>取消正在运行的补图任务（已回写的本地图片保留）。</summary>
        public TData CancelBackfillImage(string jobId)
        {
            TData obj = new TData();
            if (!PanSourceCollectImageJobManager.Cancel(jobId))
            {
                obj.Message = "补图任务不存在或已结束";
                return obj;
            }
            obj.Tag = 1;
            obj.Message = "已提交取消请求，正在停止补图";
            return obj;
        }

        #endregion

        #region 提交数据

        public async Task<TData<string>> SaveForm(PanSourceCollectEntity entity)
        {
            TData<string> obj = new TData<string>();
            if (entity == null)
            {
                obj.Message = "参数不能为空";
                return obj;
            }

            entity.CollectSource = (entity.CollectSource ?? "").Trim();
            entity.CollectSourceUrl = (entity.CollectSourceUrl ?? "").Trim();
            // 分类资源 Id：0 / 负数统一视为未分类
            if (entity.SourceCategoryId != null && entity.SourceCategoryId.Value <= 0)
            {
                entity.SourceCategoryId = null;
            }
            entity.ResourceName = (entity.ResourceName ?? "").Trim();
            entity.ResourceIntro = (entity.ResourceIntro ?? "").Trim();
            entity.ResourceUrl = (entity.ResourceUrl ?? "").Trim();
            entity.CollectImageUrl = (entity.CollectImageUrl ?? "").Trim();

            if (entity.ResourceName.Length == 0)
            {
                obj.Message = "请填写资源名称";
                return obj;
            }
            if (entity.ResourceName.Length > 500)
            {
                obj.Message = "资源名称不能超过 500 个字符";
                return obj;
            }
            if (entity.ResourceUrl.Length == 0)
            {
                obj.Message = "请填写资源链接";
                return obj;
            }
            if (entity.ResourceUrl.Length > 1000)
            {
                obj.Message = "资源链接不能超过 1000 个字符";
                return obj;
            }
            // 采集源链接、图片链接为选填：填写了才校验格式与长度
            if (entity.CollectSourceUrl.Length > 1000)
            {
                obj.Message = "采集源链接不能超过 1000 个字符";
                return obj;
            }
            if (entity.CollectSourceUrl.Length > 0 && !SiteCollectUrlRules.IsHttpUrl(entity.CollectSourceUrl))
            {
                obj.Message = "采集源链接需以 http:// 或 https:// 开头";
                return obj;
            }
            // 原图片：多图以换行或分号分隔（前端一行一个链接），最多 5 张，地址必须是 http(s) 外链
            if (entity.CollectImageUrl.Length > MaxImageUrlLength)
            {
                obj.Message = "原图片地址不能超过 1000 个字符";
                return obj;
            }
            string[] collectImages = SplitImageUrls(entity.CollectImageUrl);
            if (collectImages.Length > MaxImageCount)
            {
                obj.Message = "原图片最多 5 张，请一行一个链接";
                return obj;
            }
            foreach (string collectImage in collectImages)
            {
                if (!SiteCollectUrlRules.IsHttpUrl(collectImage))
                {
                    obj.Message = "原图片地址需以 http:// 或 https:// 开头，一行一个链接";
                    return obj;
                }
            }
            entity.CollectImageUrl = string.Join(";", collectImages);
            // 本地图片由补图任务回写，编辑表单已移除该字段：
            // 未提交(null)表示不修改，保留库中原值，避免整行更新把补图回写的数据清空；非 null 时才校验并归一化
            if (entity.ImageUrl != null)
            {
                entity.ImageUrl = entity.ImageUrl.Trim();
                if (entity.ImageUrl.Length > MaxImageUrlLength)
                {
                    obj.Message = "本地图片地址不能超过 1000 个字符";
                    return obj;
                }
                entity.ImageUrl = string.Join(";", SplitImageUrls(entity.ImageUrl));
            }
            else if (entity.Id > 0)
            {
                PanSourceCollectEntity current = await panSourceCollectService.GetEntity(entity.Id.Value);
                if (current != null)
                {
                    entity.ImageUrl = current.ImageUrl;
                }
            }
            if (entity.CollectSource.Length > 100)
            {
                obj.Message = "采集来源不能超过 100 个字符";
                return obj;
            }
            if (entity.ResourceIntro.Length > 2000)
            {
                obj.Message = "资源简介不能超过 2000 个字符";
                return obj;
            }

            // 分类资源必须存在：弹窗可能停留较久，避免提交已删除的分类
            if (entity.SourceCategoryId != null)
            {
                TData<PanSourceCategoryEntity> categoryResult = await panSourceCategoryBLL.GetEntity(entity.SourceCategoryId.Value);
                if (categoryResult.Data == null)
                {
                    obj.Message = "分类资源不存在或已被删除，请重新选择";
                    return obj;
                }
            }

            // 链接是否有效：未指定时默认有效
            if (entity.IsLinkValid == null)
            {
                entity.IsLinkValid = 1;
            }

            await panSourceCollectService.SaveForm(entity);

            obj.Data = entity.Id.ParseToString();
            obj.Tag = 1;
            obj.Message = "保存成功";
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择要删除的资源";
                return obj;
            }
            await panSourceCollectService.DeleteForm(ids);
            obj.Tag = 1;
            obj.Message = "删除成功";
            return obj;
        }

        #endregion

        #region 私有方法

        /// <summary>拆分多图片链接：兼容换行（前端一行一个）与英文分号（历史数据），去空、去重。</summary>
        private static string[] SplitImageUrls(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }
            return value
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(image => image.Trim())
                .Where(image => image.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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

        #endregion
    }
}
