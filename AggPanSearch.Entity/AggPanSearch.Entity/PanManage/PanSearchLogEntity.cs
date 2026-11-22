using System;
using Newtonsoft.Json;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 描 述：搜索日志实体（pan_search_log）
    /// 由 /api/other/web_search 每次搜索写入一条，前台"今日搜索/总搜索量"按本表统计
    /// </summary>
    [Table("pan_search_log")]
    public class PanSearchLogEntity : BaseEntity
    {
        /// <summary>搜索关键词</summary>
        public string Keyword { get; set; }

        /// <summary>搜索来源IP</summary>
        public string Ip { get; set; }

        /// <summary>是否命中缓存：1=是（直接返回缓存结果，未发起真实搜索），0=否</summary>
        [Description("是否命中缓存")]
        public int? IsCache { get; set; }

        /// <summary>搜索结果总数（AggPanSearch 返回的 total）</summary>
        [Description("搜索结果总数")]
        public int? ResultTotal { get; set; }

        /// <summary>分类网盘搜索结果，形如：夸克网盘:12;百度网盘:3</summary>
        [Description("分类网盘搜索结果")]
        public string PanTypeResults { get; set; }

        /// <summary>搜索耗时（毫秒）</summary>
        [Description("搜索耗时(ms)")]
        public int? CostMs { get; set; }

        /// <summary>搜索时间</summary>
        [JsonConverter(typeof(DateTimeJsonConverter))]
        [Description("搜索时间")]
        public DateTime? CreateTime { get; set; }
    }
}