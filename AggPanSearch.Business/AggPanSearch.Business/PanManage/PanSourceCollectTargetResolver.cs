using System;
using AggPanSearch.CollectSite;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：一次采集任务的目标（采集模式、入口地址、分页模板、起始页码）。
    /// </summary>
    public sealed class PanSourceCollectTarget
    {
        public SiteCollectMode Mode { get; init; }

        /// <summary>入口地址：详情页采集为详情页，列表类采集为列表页地址。</summary>
        public string TargetUrl { get; init; } = string.Empty;

        /// <summary>分页链接模板，仅分页列表采集有值。</summary>
        public string PageTemplate { get; init; } = string.Empty;

        /// <summary>起始页码，分页列表采集按所填列表页地址推导。</summary>
        public int StartPage { get; init; } = 1;
    }

    /// <summary>
    /// 描 述：把采集弹窗里手动填写的参数解析成具体的采集目标。
    /// 三种模式的地址均由用户手输，不再读取站点配置；分页采集的起始页码由分页模板推导。
    /// </summary>
    public static class PanSourceCollectTargetResolver
    {
        /// <summary>
        /// 解析采集目标，返回 false 时 error 为可直接展示给用户的提示。
        /// mode：0 详情页采集 / 1 单个列表页采集 / 2 分页列表采集。
        /// detailUrl：详情页地址，仅模式 0 使用。
        /// listUrl：列表页地址，模式 1 为入口地址，模式 2 为起始列表页地址。
        /// pageTemplate：分页链接模板（含 {page}），仅模式 2 使用。
        /// </summary>
        public static bool TryResolve(
            SiteCollectMode mode,
            string detailUrl,
            string listUrl,
            string pageTemplate,
            out PanSourceCollectTarget target,
            out string error)
        {
            target = new PanSourceCollectTarget();
            error = string.Empty;

            string detail = (detailUrl ?? string.Empty).Trim();
            if (detail.Length > 0 && !SiteCollectUrlRules.IsHttpUrl(detail))
            {
                error = "详情页地址需以 http:// 或 https:// 开头";
                return false;
            }

            string list = (listUrl ?? string.Empty).Trim();
            if (list.Length > 0 && !SiteCollectUrlRules.IsHttpUrl(list))
            {
                error = "列表页地址需以 http:// 或 https:// 开头";
                return false;
            }

            string template = (pageTemplate ?? string.Empty).Trim();
            string targetUrl;
            string resolvedTemplate = string.Empty;
            int startPage = 1;

            switch (mode)
            {
                case SiteCollectMode.DetailPage:
                    targetUrl = detail;
                    if (targetUrl.Length == 0)
                    {
                        error = "请输入详情页地址";
                        return false;
                    }
                    break;

                case SiteCollectMode.PagedList:
                    targetUrl = list;
                    if (targetUrl.Length == 0)
                    {
                        error = "请输入列表页地址";
                        return false;
                    }
                    if (template.Length == 0)
                    {
                        error = "请输入分页链接模板，需包含 {page} 占位符";
                        return false;
                    }
                    if (!SiteCollectPageTemplate.TryParse(template, out SiteCollectPageTemplate parsed, out error))
                    {
                        return false;
                    }
                    if (!parsed.TryResolvePageNo(targetUrl, string.Empty, out startPage, out error))
                    {
                        return false;
                    }
                    resolvedTemplate = template;
                    break;

                default:
                    targetUrl = list;
                    if (targetUrl.Length == 0)
                    {
                        error = "请输入列表页地址";
                        return false;
                    }
                    break;
            }

            if (!SiteCollectUrlRules.IsHttpUrl(targetUrl))
            {
                error = "采集地址需以 http:// 或 https:// 开头，请检查填写的地址";
                return false;
            }

            target = new PanSourceCollectTarget
            {
                Mode = mode,
                TargetUrl = targetUrl,
                PageTemplate = resolvedTemplate,
                StartPage = startPage,
            };
            return true;
        }
    }
}
