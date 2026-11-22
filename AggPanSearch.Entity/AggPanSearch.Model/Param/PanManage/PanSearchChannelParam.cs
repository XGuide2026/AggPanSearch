using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using AggPanSearch.Util;

namespace AggPanSearch.Model.Param.PanManage
{
    /// <summary>
    /// 描 述：TG频道查询类（pan_search_channel）
    /// </summary>
    public class PanSearchChannelListParam
    {
        /// <summary>
        /// 频道名（模糊匹配）
        /// </summary>
        public string name { get; set; }

        /// <summary>
        /// 分组（精确匹配）
        /// </summary>
        public string category { get; set; }

        /// <summary>
        /// 状态：1启用 0停用，null 表示不限
        /// </summary>
        public int? status { get; set; }

        /// <summary>
        /// 检测资源筛选（硬编码，空表示全部）：
        /// today=今日有更新 none=无网盘资源 inaccessible=不可访问
        /// week=最近1周 month=最近1个月 halfyear=最近6个月 year=最近一年
        /// </summary>
        public string probe { get; set; }
    }
}
