using System;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 描 述：版权投诉查询类（pan_copyright_complaint）
    /// </summary>
    public class PanCopyrightComplaintListParam
    {
        /// <summary>
        /// 作品名称（模糊匹配）
        /// </summary>
        public string title { get; set; }

        /// <summary>
        /// 联系方式（模糊匹配）
        /// </summary>
        public string contact { get; set; }

        /// <summary>
        /// 提交 IP（模糊匹配）
        /// </summary>
        public string complaint_ip { get; set; }

        /// <summary>
        /// 状态：0待处理 1已通过 2已驳回，null 表示不限
        /// </summary>
        public int? status { get; set; }

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
