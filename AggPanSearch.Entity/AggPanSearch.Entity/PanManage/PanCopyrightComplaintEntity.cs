using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 描 述：版权投诉实体（pan_copyright_complaint）。
    /// 前台「版权声明 · 侵权投诉」提交后写入，后台「网盘管理 - 版权投诉」审核；
    /// 审核通过的作品名会同步到 pan_conf.copyright_block_keywords，参与搜索结果过滤。
    /// </summary>
    [Table("pan_copyright_complaint")]
    public class PanCopyrightComplaintEntity : BaseModifyEntity
    {
        /// <summary>被侵权作品名称（审核通过后作为版权屏蔽关键词）</summary>
        [Description("作品名称")]
        public string Title { get; set; }

        /// <summary>侵权链接（投诉人填写的具体侵权资源地址）</summary>
        [Description("侵权链接")]
        public string InfringingUrl { get; set; }

        /// <summary>投诉人 / 权利人</summary>
        [Description("投诉人")]
        public string Complainant { get; set; }

        /// <summary>联系方式（手机 / 邮箱 / 其他）</summary>
        [Description("联系方式")]
        public string Contact { get; set; }

        /// <summary>侵权情况说明</summary>
        [Description("侵权说明")]
        public string Description { get; set; }

        /// <summary>证明材料图片相对路径，英文逗号分隔，1~3 张</summary>
        [Description("证明材料")]
        public string EvidenceImages { get; set; }

        /// <summary>提交 IP</summary>
        [Description("提交IP")]
        public string ComplaintIp { get; set; }

        /// <summary>状态：0待处理 1已通过 2已驳回</summary>
        [Description("状态")]
        public int? Status { get; set; }

        /// <summary>处理备注（审核通过/驳回时填写）</summary>
        [Description("处理备注")]
        public string AuditRemark { get; set; }

        /// <summary>审核时间</summary>
        [JsonConverter(typeof(DateTimeJsonConverter))]
        [Description("审核时间")]
        public DateTime? AuditTime { get; set; }

        /// <summary>审核人 Id</summary>
        [Description("审核人")]
        public long? AuditUserId { get; set; }
    }
}