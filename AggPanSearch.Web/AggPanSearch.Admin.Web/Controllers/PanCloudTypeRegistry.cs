using System;
using System.Collections.Generic;
using System.Linq;
using AggPanSearch.Models;

namespace AggPanSearch.Admin.Web.Controllers
{
    /// <summary>
    /// 单个网盘类型定义：AggPanSearch 类型名 + 中文名 + 前端数值 is_type。
    /// </summary>
    public sealed class PanCloudType
    {
        public PanCloudType(string value, string text, int isType)
        {
            Value = value;
            Text = text;
            IsType = isType;
        }

        /// <summary>AggPanSearch 类型名，如 quark（对应 CloudTypes 常量）。</summary>
        public string Value { get; }

        /// <summary>中文名，如 夸克网盘。</summary>
        public string Text { get; }

        /// <summary>前端数值 is_type，与前台网盘类型筛选标签的下标一一对应。</summary>
        public int IsType { get; }
    }

    /// <summary>
    /// 网盘类型注册表：后台「搜索设置 - 网盘类型」页签与前台搜索共用，避免各处硬编码类型清单。
    /// 类型集合来自 AggPanSearch 的 CloudTypes 常量（唯一来源），此处只补充中文名与前端 is_type。
    /// 说明：不新增数据表，启用状态仍保存在 pan_conf 的 search_cloud_types（逗号分隔）。
    /// </summary>
    public static class PanCloudTypeRegistry
    {
        /// <summary>
        /// 有序定义：数组下标即前端 is_type（0 起），顺序与前台筛选标签保持一致，
        /// 新增网盘类型时只需在 AggPanSearch 的 CloudTypes 里加常量，再补一行中文名即可。
        /// </summary>
        private static readonly string[] OrderedValues =
        {
            CloudTypes.Quark, CloudTypes.Aliyun, CloudTypes.Baidu, CloudTypes.Uc,
            CloudTypes.Xunlei, CloudTypes.Pan123, CloudTypes.Pan115, CloudTypes.Tianyi,
            CloudTypes.Mobile, CloudTypes.Magnet, CloudTypes.Guangya, CloudTypes.Pikpak,
            CloudTypes.Ed2k, CloudTypes.Others,
        };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudTypes.Quark] = "夸克网盘",
            [CloudTypes.Aliyun] = "阿里云盘",
            [CloudTypes.Baidu] = "百度网盘",
            [CloudTypes.Uc] = "UC网盘",
            [CloudTypes.Xunlei] = "迅雷云盘",
            [CloudTypes.Pan123] = "123云盘",
            [CloudTypes.Pan115] = "115网盘",
            [CloudTypes.Tianyi] = "天翼云盘",
            [CloudTypes.Mobile] = "移动云盘",
            [CloudTypes.Magnet] = "磁力链接",
            [CloudTypes.Guangya] = "光鸭网盘",
            [CloudTypes.Pikpak] = "PikPak",
            [CloudTypes.Ed2k] = "电驴链接",
            [CloudTypes.Others] = "其它网盘",
        };

        /// <summary>全部网盘类型（按 is_type 升序）。</summary>
        public static IReadOnlyList<PanCloudType> All { get; } = OrderedValues
            .Select((value, index) => new PanCloudType(value, Names.TryGetValue(value, out string name) ? name : value, index))
            .ToList();

        /// <summary>按 AggPanSearch 类型名查找；未注册返回 null。</summary>
        public static PanCloudType Find(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            string key = value.Trim().ToLowerInvariant();
            return All.FirstOrDefault(t => string.Equals(t.Value, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>AggPanSearch 类型名 -> 前端 is_type；未注册返回 -1。</summary>
        public static int ToIsType(string value)
        {
            PanCloudType type = Find(value);
            return type == null ? -1 : type.IsType;
        }

        /// <summary>前端 is_type -> AggPanSearch 类型名；未注册返回 null。</summary>
        public static string ToValue(int isType)
        {
            PanCloudType type = All.FirstOrDefault(t => t.IsType == isType);
            return type == null ? null : type.Value;
        }

        /// <summary>前端 is_type -> 中文名；未注册返回空字符串。</summary>
        public static string ToText(int isType)
        {
            PanCloudType type = All.FirstOrDefault(t => t.IsType == isType);
            return type == null ? "" : type.Text;
        }
    }
}
