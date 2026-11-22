using System;
using System.Collections.Generic;
using System.Linq;
using AggPanSearch.CollectSite;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集站点字段校验。
    /// 站点保存名称与站点链接（均必填）；分页模板、详情页地址在采集弹窗中手动填写，由 PanSourceCollectTargetResolver 校验。
    /// 站点分类配置项由 <see cref="NormalizeCategories"/> 校验。
    /// </summary>
    public static class PanSourceCollectSiteValidator
    {
        public const int MaxSiteNameLength = 100;
        public const int MaxSiteUrlLength = 1000;

        /// <summary>站点分类名称长度上限。</summary>
        public const int MaxCategoryNameLength = 50;

        /// <summary>站点分类地址长度上限。</summary>
        public const int MaxCategoryUrlLength = 1000;

        /// <summary>单个站点允许配置的分类条数上限。</summary>
        public const int MaxCategoryCount = 100;

        /// <summary>校验站点配置：名称与站点链接必填、链接须为 http(s)、名称不可重名（重名会导致采集来源无法区分）。</summary>
        public static bool Validate(PanSourceCollectSite entity, out string message)
        {
            message = string.Empty;
            if (entity == null)
            {
                message = "参数不能为空";
                return false;
            }

            entity.SiteName = (entity.SiteName ?? string.Empty).Trim();
            if (entity.SiteName.Length == 0)
            {
                message = "请填写站点名称";
                return false;
            }
            if (entity.SiteName.Length > MaxSiteNameLength)
            {
                message = "站点名称不能超过 " + MaxSiteNameLength + " 个字符";
                return false;
            }

            entity.SiteUrl = (entity.SiteUrl ?? string.Empty).Trim();
            if (entity.SiteUrl.Length == 0)
            {
                message = "请填写站点链接";
                return false;
            }
            if (entity.SiteUrl.Length > MaxSiteUrlLength)
            {
                message = "站点链接不能超过 " + MaxSiteUrlLength + " 个字符";
                return false;
            }
            if (!SiteCollectUrlRules.IsHttpUrl(entity.SiteUrl))
            {
                message = "站点链接需以 http:// 或 https:// 开头";
                return false;
            }

            List<PanSourceCollectSite> sites = PanSourceCollectSiteStore.GetAll();
            bool duplicated = sites.Any(t =>
                !string.Equals(t.Id, entity.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((t.SiteName ?? string.Empty).Trim(), entity.SiteName, StringComparison.OrdinalIgnoreCase));
            if (duplicated)
            {
                message = "该站点名称已存在，请勿重复添加";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 规范化并校验站点分类配置：去空白、名称必填且不超长、地址可选但须为 http(s)、
        /// 单站点条数上限，并按「地址 + 名称」去重。校验失败时 message 为可直接提示的中文原因。
        /// </summary>
        public static bool NormalizeCategories(List<PanSourceCollectSiteCategory> categories, out List<PanSourceCollectSiteCategory> normalized, out string message)
        {
            message = string.Empty;
            normalized = new List<PanSourceCollectSiteCategory>();
            if (categories == null)
            {
                return true;
            }
            if (categories.Count > MaxCategoryCount)
            {
                message = "站点分类最多配置 " + MaxCategoryCount + " 项";
                return false;
            }

            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PanSourceCollectSiteCategory item in categories)
            {
                if (item == null)
                {
                    continue;
                }
                string name = (item.Name ?? string.Empty).Trim();
                string url = (item.Url ?? string.Empty).Trim();
                string sourceName = (item.SourceCategoryName ?? string.Empty).Trim();

                // 完全空白的行视为未填写，直接忽略，避免保存出一堆无效行
                if (name.Length == 0 && url.Length == 0 && item.SourceCategoryId <= 0)
                {
                    continue;
                }
                if (name.Length == 0)
                {
                    message = "站点分类名称不能为空";
                    return false;
                }
                if (name.Length > MaxCategoryNameLength)
                {
                    message = "站点分类名称不能超过 " + MaxCategoryNameLength + " 个字符";
                    return false;
                }
                if (url.Length > MaxCategoryUrlLength)
                {
                    message = "站点分类地址不能超过 " + MaxCategoryUrlLength + " 个字符";
                    return false;
                }
                if (url.Length > 0 && !SiteCollectUrlRules.IsHttpUrl(url))
                {
                    message = "站点分类地址需以 http:// 或 https:// 开头";
                    return false;
                }
                if (!keys.Add(name + "\n" + url))
                {
                    message = "存在重复的站点分类「" + name + "」，请检查后重试";
                    return false;
                }
                if (item.SourceCategoryId < 0)
                {
                    message = "站点分类「" + name + "」绑定的分类资源无效";
                    return false;
                }

                normalized.Add(new PanSourceCollectSiteCategory
                {
                    Name = name,
                    Url = url,
                    SourceCategoryId = item.SourceCategoryId,
                    SourceCategoryName = sourceName,
                });
            }
            return true;
        }
    }
}
