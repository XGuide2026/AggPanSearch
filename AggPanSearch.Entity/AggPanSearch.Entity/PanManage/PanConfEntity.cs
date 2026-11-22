using System;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations.Schema;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-03 03:01
    /// 描 述：实体类
    /// </summary>
    [Table("pan_conf")]
    public class PanConfEntity : BaseEntity
    {
        /// <summary>单选多选等文本类型的数据集</summary>
        public string ConfContent { get; set; }
        /// <summary>创建时间戳</summary>
        public int? ConfCreatetime { get; set; }
        /// <summary>参数描述</summary>
        public string ConfDesc { get; set; }
        /// <summary>参数到期</summary>
        public int? ConfInt { get; set; }
        /// <summary>参数名</summary>
        public string ConfKey { get; set; }
        /// <summary>排序</summary>
        public int? ConfSort { get; set; }
        /// <summary>文本类型</summary>
        public int? ConfSpec { get; set; }
        /// <summary>显示隐藏 0是隐藏</summary>
        public int? ConfStatus { get; set; }
        /// <summary>1为系统参数，请勿删除</summary>
        public int? ConfSystem { get; set; }
        /// <summary>参数名称</summary>
        public string ConfTitle { get; set; }
        /// <summary>配置分类 </summary>
        public int? ConfType { get; set; }
        /// <summary>更新时间戳</summary>
        public int? ConfUpdatetime { get; set; }
        /// <summary>参数值</summary>
        public string ConfValue { get; set; }
        /// <summary>冗余主键引用，仅用于兼容旧接口/前端取数；主键以基类 Id 为准，不映射数据库列</summary>
        [JsonConverter(typeof(StringJsonConverter))]
        [NotMapped]
        public long? id { get; set; }
    }
}