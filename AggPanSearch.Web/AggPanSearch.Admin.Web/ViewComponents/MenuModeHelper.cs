using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AggPanSearch.Entity.SystemManage;
using AggPanSearch.Util;

namespace AggPanSearch.Admin.Web.ViewComponents
{
    /// <summary>
    /// 开发模式 / 管理模式 菜单切换
    /// 开发模式：只显示开发相关的一级菜单（首页 + appsettings.json 里 MenuMode:DevMenus 配置的一级菜单）
    /// 管理模式：只显示其余的一级菜单（默认排除 DevMenus 里的菜单；配置了 MenuMode:ManageMenus 则按白名单显示）
    /// 当前模式保存在 Cookie 中，点击顶部栏按钮切换后刷新页面生效
    /// </summary>
    public static class MenuModeHelper
    {
        public const string CookieName = "MenuMode";

        public const string DevMode = "dev";

        public const string ManageMode = "manage";

        private const string DevMenuConfigKey = "MenuMode:DevMenus";

        private const string ManageMenuConfigKey = "MenuMode:ManageMenus";

        /// <summary>
        /// 未配置时开发模式默认显示的一级菜单，首页在布局里固定输出，不在此列
        /// </summary>
        private static readonly string[] DefaultDevMenuNames = new string[] { "单位组织", "系统管理", "系统工具", "实例演示" };

        /// <summary>
        /// 获取当前模式，未设置或非法值一律按开发模式处理
        /// </summary>
        public static string GetMode(HttpContext httpContext)
        {
            string mode = httpContext?.Request?.Cookies[CookieName];
            if (!string.IsNullOrEmpty(mode) && string.Equals(mode.Trim(), ManageMode, StringComparison.OrdinalIgnoreCase))
            {
                return ManageMode;
            }
            return DevMode;
        }

        public static bool IsManageMode(HttpContext httpContext)
        {
            return GetMode(httpContext) == ManageMode;
        }

        /// <summary>
        /// 切换后要进入的模式
        /// </summary>
        public static string GetTargetMode(HttpContext httpContext)
        {
            return IsManageMode(httpContext) ? DevMode : ManageMode;
        }

        public static string GetModeName(string mode)
        {
            return mode == ManageMode ? "管理模式" : "开发模式";
        }

        /// <summary>
        /// 开发模式下显示的一级菜单名称
        /// </summary>
        public static List<string> GetDevMenuNames()
        {
            List<string> names = ReadConfigNames(DevMenuConfigKey);
            if (names.Count == 0)
            {
                names = DefaultDevMenuNames.ToList();
            }
            return names;
        }

        /// <summary>
        /// 管理模式下显示的一级菜单名称，为空表示“除开发模式菜单外的全部”
        /// </summary>
        public static List<string> GetManageMenuNames()
        {
            return ReadConfigNames(ManageMenuConfigKey);
        }

        /// <summary>
        /// 按模式过滤菜单，只过滤一级菜单，子菜单跟随一级菜单保留或隐藏
        /// </summary>
        public static List<MenuEntity> FilterByMode(List<MenuEntity> menuList, string mode)
        {
            if (menuList == null || menuList.Count == 0)
            {
                return menuList;
            }
            List<string> devMenuNames = GetDevMenuNames();
            List<string> manageMenuNames = GetManageMenuNames();
            bool isManageMode = mode == ManageMode;

            List<MenuEntity> topMenuList = menuList.Where(p => (p.ParentId ?? 0) == 0).ToList();
            HashSet<long?> keptIds = new HashSet<long?>();
            foreach (MenuEntity menu in topMenuList)
            {
                string menuName = (menu.MenuName ?? string.Empty).Trim();
                bool keep;
                if (isManageMode)
                {
                    keep = manageMenuNames.Count > 0
                        ? manageMenuNames.Contains(menuName)
                        : !devMenuNames.Contains(menuName);
                }
                else
                {
                    keep = devMenuNames.Contains(menuName);
                }
                if (keep)
                {
                    keptIds.Add(menu.Id);
                }
            }

            // 逐层把保留下来的菜单的子菜单也加进来，直到没有新的为止
            bool hasNew = true;
            while (hasNew)
            {
                hasNew = false;
                foreach (MenuEntity menu in menuList)
                {
                    if (!keptIds.Contains(menu.Id) && menu.ParentId != null && keptIds.Contains(menu.ParentId))
                    {
                        keptIds.Add(menu.Id);
                        hasNew = true;
                    }
                }
            }
            return menuList.Where(p => keptIds.Contains(p.Id)).ToList();
        }

        private static List<string> ReadConfigNames(string key)
        {
            List<string> names = new List<string>();
            IConfigurationSection section = GlobalContext.Configuration?.GetSection(key);
            if (section == null)
            {
                return names;
            }
            foreach (IConfigurationSection item in section.GetChildren())
            {
                string name = (item.Value ?? string.Empty).Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
            return names;
        }
    }
}
