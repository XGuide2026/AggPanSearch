using System;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 03:11
    /// 描 述：实体类
    /// </summary>
    [Table("pan_source")]
    public class PanSourceEntity : BaseModifyEntity
    {
        /// <summary>提取码</summary>
        public string Code { get; set; }
        /// <summary>转存原链接（转存前的原始分享链接，用于去重）</summary>
        public string OriginalUrl { get; set; }
        /// <summary>夸克标识</summary>
        public string Fid { get; set; }
        /// <summary>是否删除 0=正常 1=软删除</summary>
        public byte? IsDelete { get; set; }
        /// <summary>0正常 1临时文件</summary>
        public int? IsTime { get; set; }
        /// <summary>是否置顶 0=否 1=是</summary>
        public byte? IsTop { get; set; }
        /// <summary>0夸克网盘 1阿里网盘 2百度网盘 3UC网盘 4迅雷网盘</summary>
        public int? IsType { get; set; }
        /// <summary>状态 0=后台添加 1=用户添加</summary>
        public byte? IsUser { get; set; }
        /// <summary>浏览量</summary>
        public int? PageViews { get; set; }
        /// <summary>排序</summary>
        public int? Sort { get; set; }
        /// <summary>分类ID</summary>
        public int? SourceCategoryId { get; set; }
        /// <summary>状态 0=禁用 1=启用</summary>
        public int? Status { get; set; }
        /// <summary>资源名称</summary>
        [Required(ErrorMessage = "资源名称不能为空")]
        [StringLength(255, ErrorMessage = "资源名称长度不能超过255个字符")]
        public string Title { get; set; }
        /// <summary>资源地址</summary>
        [Required(ErrorMessage = "资源地址不能为空")]
        public string Url { get; set; }
        /// <summary>资源介绍</summary>
        public string VodContent { get; set; }
        /// <summary>资源图片</summary>
        public string VodPic { get; set; }
        /// <summary>文件个数：转存时从采集表拷贝（资源信息分析采集），NULL 表示未分析</summary>
        public int? FileCount { get; set; }
        /// <summary>文件大小：单位 M（转存时从采集表拷贝，按 1024 进制换算，如 6.2G 存为 6349），NULL 表示未分析</summary>
        public int? FileSize { get; set; }
        /// <summary>资源有效期：网盘分享页原文（如 永久有效）</summary>
        public string ShareValidityPeriod { get; set; }
    }
}