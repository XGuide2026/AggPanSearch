using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using AggPanSearch.Util;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-04 03:11
    /// 描 述：实体查询类
    /// </summary>
    public class PanSourceListParam
    {
        /// <summary>
        /// 资源分类ID
        /// </summary>
        public int? source_category_id { get; set; }
        /// <summary>
        /// 搜索关键词（模糊匹配 资源名称/资源地址/资源介绍）
        /// </summary>
        public string keyword { get; set; }
    }
}
