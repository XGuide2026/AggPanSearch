using System;
using Newtonsoft.Json;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 描 述：TG频道实体（pan_search_channel）。
    /// 后台「网盘管理 - TG频道」维护，前台 /api/other/web_search 读取启用状态的频道参与全网搜索。
    /// 表为空时前台自动回退到 AggPanSearch 内置的默认频道列表。
    /// </summary>
    [Table("pan_search_channel")]
    public class PanSearchChannelEntity : BaseModifyEntity
    {
        /// <summary>频道名（t.me/s/ 后面的部分，如 Aliyun_4K_Movies）</summary>
        [Description("频道名")]
        public string Name { get; set; }

        /// <summary>分组（如 影视 / 短剧 / 综合），仅用于后台筛选，不参与搜索</summary>
        [Description("分组")]
        public string Category { get; set; }

        /// <summary>排序，数值越小越靠前</summary>
        [Description("排序")]
        public int? Sort { get; set; }

        /// <summary>状态：1启用（参与搜索） 0停用</summary>
        [Description("状态")]
        public int? Status { get; set; }

        /// <summary>是否内置频道：1禁止删除（系统默认频道），0可删除</summary>
        [Description("是否内置")]
        public int? IsSys { get; set; }

        /// <summary>权重，预留：用于后续按权重分配并发/结果排序</summary>
        [Description("权重")]
        public int? Weight { get; set; }

        /// <summary>最近一次抓取成功时间（预留，便于识别失效频道）</summary>
        [JsonConverter(typeof(DateTimeJsonConverter))]
        [Description("最近成功时间")]
        public DateTime? LastOkTime { get; set; }

        /// <summary>最近一次发布网盘资源链接的时间（后台列表检测时自动刷新）</summary>
        [JsonConverter(typeof(DateTimeJsonConverter))]
        [Description("最近资源时间")]
        public DateTime? LastUpdate { get; set; }

        /// <summary>最近一次检测结果：1可访问 0不可访问 null未检测（后台列表检测时自动刷新）</summary>
        [Description("检测结果")]
        public int? ProbeStatus { get; set; }

        /// <summary>备注</summary>
        [Description("备注")]
        public string Remark { get; set; }
    }
}