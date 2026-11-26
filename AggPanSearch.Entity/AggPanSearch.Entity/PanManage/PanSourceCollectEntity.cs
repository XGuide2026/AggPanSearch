using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 描 述：采集资源实体（pan_source_collect）。
    /// 后台「网盘管理 - 采集资源」维护采集到的资源条目。
    /// </summary>
    [Table("pan_source_collect")]
    public class PanSourceCollectEntity : BaseModifyEntity
    {
        /// <summary>采集来源（如 TG 频道名、站点名称）</summary>
        [Description("采集来源")]
        public string CollectSource { get; set; }

        /// <summary>采集源链接（来源页面地址）</summary>
        [Description("采集源链接")]
        public string CollectSourceUrl { get; set; }

        /// <summary>分类资源 Id（pan_source_category.Id），NULL 表示未分类</summary>
        [Description("分类资源ID")]
        public long? SourceCategoryId { get; set; }

        /// <summary>资源名称</summary>
        [Description("资源名称")]
        public string ResourceName { get; set; }

        /// <summary>资源简介</summary>
        [Description("资源简介")]
        public string ResourceIntro { get; set; }

        /// <summary>资源链接（网盘分享地址）</summary>
        [Description("资源链接")]
        public string ResourceUrl { get; set; }

        /// <summary>链接是否有效：-1 未检测 1 有效 0 无效（新增/采集默认 -1，资源分析后回写 1 或 0）</summary>
        [Description("链接是否有效")]
        public int? IsLinkValid { get; set; }

        /// <summary>原图片地址：采集到的远端图片，多张用英文分号分隔（最多 5 张）</summary>
        [Description("原图片地址")]
        public string CollectImageUrl { get; set; }

        /// <summary>本地图片地址：从 CollectImageUrl 下载到本机 Resource/Collect 目录下的文件，
        /// 多张用英文分号分隔，与 CollectImageUrl 同索引一一对应</summary>
        [Description("本地图片地址")]
        public string ImageUrl { get; set; }

        /// <summary>文件个数：资源信息分析采集（网盘分享页 span.info-stat 中「共 N 个文件」），NULL 表示未分析</summary>
        [Description("文件个数")]
        public int? FileCount { get; set; }

        /// <summary>文件大小：单位 M（资源信息分析采集，按 1024 进制换算，如 6.2G 存为 6349），NULL 表示未分析</summary>
        [Description("文件大小")]
        public int? FileSize { get; set; }

        /// <summary>资源有效期：网盘分享页原文（如 永久有效）</summary>
        [Description("资源有效期")]
        public string ShareValidityPeriod { get; set; }
    }
}