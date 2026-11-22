using System;
using System.Collections.Generic;
using AggPanSearch.Model.Param.SystemManage;

namespace AggPanSearch.Model.Param.OrganizationManage
{
    public class NewsListParam : BaseAreaParam
    {
        public string NewsTitle { get; set; }
        public int? NewsType { get; set; }
        public string NewsTag { get; set; }
    }
}
