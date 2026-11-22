using System;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations.Schema;
using AggPanSearch.Util;

namespace AggPanSearch.Entity.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 02:05
    /// 描 述：实体类
    /// </summary>
    [Table("pan_source_category")]
    public class PanSourceCategoryEntity : BaseModifyEntity
    {

        /// <summary>分类图片</summary>
        public string Image { get; set; }
        /// <summary>1时不能删除</summary>
        public int? IsSys { get; set; }
        /// <summary>0网络, 1本地</summary>
        public int? IsType { get; set; }
        /// <summary>0不更新 1更新</summary>
        public int? IsUpdate { get; set; }
        /// <summary>分类名称</summary>
        public string Name { get; set; }
        /// <summary>排序</summary>
        public int? Sort { get; set; }
        /// <summary>状态</summary>
        public int? Status { get; set; }
    }
}