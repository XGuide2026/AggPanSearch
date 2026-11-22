using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AggPanSearch.CollectSite;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：站点分类配置业务类。
    /// 负责「配置分类」弹窗用到的三件事：抓取站点分类候选、读取「分类资源」下拉数据、
    /// 保存站点分类与「分类资源」的对应关系（随站点存入 collect-sites.json）。
    /// 该映射在采集任务启动时被 <see cref="PanSourceCollectCategoryMap"/> 读取，用于把源站分类换算成分类资源 Id。
    /// </summary>
    public class PanSourceCollectCategoryBLL
    {
        private PanSourceCategoryBLL panSourceCategoryBLL = new PanSourceCategoryBLL();

        #region 获取数据

        /// <summary>「分类资源」下拉数据（pan_source_category 全量）。</summary>
        public async Task<TData<List<PanSourceCategoryEntity>>> GetSourceCategoryList()
        {
            return await panSourceCategoryBLL.GetList(new PanSourceCategoryListParam());
        }

        /// <summary>
        /// 抓取站点分类候选：按站点配置的链接抓一次首页，用已适配解析器 / 通用兜底识别分类入口。
        /// 抓取失败时 Tag=0 并返回中文原因，不影响手工添加分类。
        /// </summary>
        public async Task<TData<List<SiteCategoryCandidate>>> FetchSiteCategories(string siteId)
        {
            TData<List<SiteCategoryCandidate>> obj = new TData<List<SiteCategoryCandidate>>();
            PanSourceCollectSite site = PanSourceCollectSiteStore.GetById(siteId);
            if (site == null)
            {
                obj.Message = "站点不存在或已被删除，请刷新页面后重试";
                return obj;
            }
            if (string.IsNullOrWhiteSpace(site.SiteUrl))
            {
                obj.Message = "站点未配置站点链接，请先编辑站点补充链接";
                return obj;
            }

            SiteCategoryFetchResult fetch = await SiteCollectCategoryFetcher.FetchAsync(site.SiteUrl);
            if (!fetch.Success)
            {
                obj.Message = fetch.Message;
                return obj;
            }

            obj.Data = fetch.Items;
            obj.Total = fetch.Items.Count;
            obj.Tag = 1;
            obj.Message = fetch.Message;
            return obj;
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 保存站点分类配置：校验分类项、按 Id 回填「分类资源」名称（防止前端伪造），
        /// 然后连同站点一起写回 JSON 文件。允许保存空列表（表示清空配置）。
        /// </summary>
        public async Task<TData<int>> SaveCategories(string siteId, List<PanSourceCollectSiteCategory> categories)
        {
            TData<int> obj = new TData<int>();
            PanSourceCollectSite site = PanSourceCollectSiteStore.GetById(siteId);
            if (site == null)
            {
                obj.Message = "站点不存在或已被删除，请刷新页面后重试";
                return obj;
            }

            if (!PanSourceCollectSiteValidator.NormalizeCategories(categories, out List<PanSourceCollectSiteCategory> normalized, out string message))
            {
                obj.Message = message;
                return obj;
            }

            // 绑定关系以数据库为准：Id 必须存在，名称用库里的真实值回填
            List<long> boundIds = normalized
                .Where(t => t.SourceCategoryId > 0)
                .Select(t => t.SourceCategoryId)
                .Distinct()
                .ToList();
            if (boundIds.Count > 0)
            {
                TData<List<PanSourceCategoryEntity>> categoryResult = await GetSourceCategoryList();
                Dictionary<long, string> nameMap = (categoryResult.Data ?? new List<PanSourceCategoryEntity>())
                    .Where(t => t.Id.HasValue)
                    .GroupBy(t => t.Id.Value)
                    .ToDictionary(g => g.Key, g => (g.First().Name ?? string.Empty).Trim());

                foreach (long id in boundIds)
                {
                    if (!nameMap.ContainsKey(id))
                    {
                        PanSourceCollectSiteCategory invalid = normalized.First(t => t.SourceCategoryId == id);
                        obj.Message = "分类资源「" + invalid.Name + "」绑定项已不存在，请重新选择";
                        return obj;
                    }
                }
                foreach (PanSourceCollectSiteCategory item in normalized)
                {
                    item.SourceCategoryName = item.SourceCategoryId > 0
                        ? nameMap[item.SourceCategoryId]
                        : string.Empty;
                }
            }
            else
            {
                foreach (PanSourceCollectSiteCategory item in normalized)
                {
                    item.SourceCategoryName = string.Empty;
                }
            }

            site.Categories = normalized;
            site.UpdateTime = DateTime.Now;
            PanSourceCollectSiteStore.Save(site);

            obj.Data = normalized.Count;
            obj.Tag = 1;
            obj.Message = normalized.Count > 0
                ? "已保存 " + normalized.Count + " 个站点分类"
                : "已清空站点分类配置";
            return obj;
        }

        #endregion
    }
}
