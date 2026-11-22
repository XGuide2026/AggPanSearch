using System;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 描 述：采集资源查询类（pan_source_collect）
    /// </summary>
    public class PanSourceCollectListParam
    {
        /// <summary>
        /// 采集来源（模糊匹配）
        /// </summary>
        public string collect_source { get; set; }

        /// <summary>
        /// 分类资源 Id（精确匹配），null 或 0 表示不限分类
        /// </summary>
        public long? source_category_id { get; set; }

        /// <summary>
        /// 资源名称（模糊匹配）
        /// </summary>
        public string resource_name { get; set; }

        /// <summary>
        /// 链接是否有效：1 有效 0 无效，null 表示不限
        /// </summary>
        public int? is_link_valid { get; set; }

        /// <summary>
        /// 开始时间（含当天 00:00:00）
        /// </summary>
        public DateTime? startTime { get; set; }

        /// <summary>
        /// 结束时间（含当天 23:59:59）
        /// </summary>
        public DateTime? endTime { get; set; }
    }
}
