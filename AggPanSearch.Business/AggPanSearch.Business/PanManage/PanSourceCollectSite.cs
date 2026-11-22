using System;
using System.Collections.Generic;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：采集站点配置（站点名称 + 站点链接，分页模板与详情页地址在采集弹窗中手动填写）。
    /// 不建数据表，整体保存在 ContentRoot/AppData/collect-sites.json。
    /// 站点分类与「分类资源」的对应关系也随站点一起保存（见 Categories）。
    /// </summary>
    public class PanSourceCollectSite
    {
        /// <summary>站点唯一标识（Guid 字符串）。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>站点名称，同时作为采集来源写入资源记录。</summary>
        public string SiteName { get; set; } = string.Empty;

        /// <summary>站点链接（首页 / 列表页地址），必填，须以 http(s) 开头。</summary>
        public string SiteUrl { get; set; } = string.Empty;

        /// <summary>
        /// 站点分类配置：站点分类（自动抓取或手工补录）与「分类资源」表的人工对应关系。
        /// 旧数据文件没有该字段时反序列化为空集合，向后兼容。
        /// </summary>
        public List<PanSourceCollectSiteCategory> Categories { get; set; } = new List<PanSourceCollectSiteCategory>();

        public DateTime CreateTime { get; set; }

        public DateTime UpdateTime { get; set; }
    }

    /// <summary>
    /// 描 述：站点分类配置项。
    /// 每一行代表站点上的一个分类入口，可绑定「分类资源」表（pan_source_category）中的一条记录；
    /// SourceCategoryId 为 0 表示尚未绑定，允许保留未绑定的分类项。
    /// </summary>
    public class PanSourceCollectSiteCategory
    {
        /// <summary>站点分类名称（自动抓取的链接文本，可手工改名）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>站点分类页地址（手工补录的分类项允许为空）。</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>绑定的「分类资源」Id，0 表示未绑定。</summary>
        public long SourceCategoryId { get; set; }

        /// <summary>绑定的「分类资源」名称（保存时按 Id 回填，便于列表展示）。</summary>
        public string SourceCategoryName { get; set; } = string.Empty;
    }
}
