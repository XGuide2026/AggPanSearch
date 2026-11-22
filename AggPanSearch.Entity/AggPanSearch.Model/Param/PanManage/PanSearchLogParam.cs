using System;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 描 述：搜索日志查询参数
    /// </summary>
    public class PanSearchLogListParam
    {
        /// <summary>
        /// 搜索关键词（模糊匹配）
        /// </summary>
        public string keyword { get; set; }

        /// <summary>
        /// 搜索来源IP（模糊匹配）
        /// </summary>
        public string ip { get; set; }

        /// <summary>
        /// 是否命中缓存：1=是，0=否，-1/空=全部
        /// </summary>
        public int? is_cache { get; set; }

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
