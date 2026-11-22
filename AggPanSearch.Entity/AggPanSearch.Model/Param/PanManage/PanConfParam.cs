using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using AggPanSearch.Util;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-03 03:01
    /// 描 述：实体查询类
    /// </summary>
    public class PanConfListParam
    {
        /// <summary>
        /// 配置分类（对应 conf_type，用于 Tab 筛选）
        /// </summary>
        public int? ConfType { get; set; }

        /// <summary>
        /// 参数名（conf_key 模糊匹配）
        /// </summary>
        public string ConfKey { get; set; }

        /// <summary>
        /// 参数名称（conf_title 模糊匹配）
        /// </summary>
        public string ConfTitle { get; set; }
    }
}
