using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Business.SystemManage;
using AggPanSearch.Entity.SystemManage;
using AggPanSearch.Enum;
using AggPanSearch.Model.Result;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Web.Code;

namespace AggPanSearch.Admin.Web.ViewComponents
{
    /// <summary>
    /// 后台左侧菜单视图组件，按当前登录用户权限输出菜单
    /// </summary>
    public class SideMenuViewComponent : ViewComponent
    {
        private MenuBLL menuBLL = new MenuBLL();
        private MenuAuthorizeBLL menuAuthorizeBLL = new MenuAuthorizeBLL();

        public async Task<IViewComponentResult> InvokeAsync()
        {
            SideMenuModel model = new SideMenuModel();
            model.CurrentPath = GetCurrentPath();
            model.MenuMode = MenuModeHelper.GetMode(ViewContext.HttpContext);

            // 布局页已经取过一次当前用户，这里直接复用，避免重复解析
            OperatorInfo operatorInfo = ViewContext.HttpContext.Items[SideMenuModel.OperatorInfoKey] as OperatorInfo;
            if (operatorInfo == null)
            {
                operatorInfo = await Operator.Instance.Current();
            }
            model.OperatorInfo = operatorInfo;

            if (operatorInfo != null)
            {
                TData<List<MenuEntity>> objMenu = await menuBLL.GetList(null);
                if (objMenu.Data != null)
                {
                    List<MenuEntity> menuList = objMenu.Data.Where(p => p.MenuStatus == StatusEnum.Yes.ParseToInt()).ToList();
                    if (operatorInfo.IsSystem != 1)
                    {
                        TData<List<MenuAuthorizeInfo>> objMenuAuthorize = await menuAuthorizeBLL.GetAuthorizeList(operatorInfo);
                        List<long?> authorizeMenuIdList = objMenuAuthorize.Data.Select(p => p.MenuId).ToList();
                        menuList = menuList.Where(p => authorizeMenuIdList.Contains(p.Id)).ToList();
                    }
                    // 按开发模式 / 管理模式过滤一级菜单
                    menuList = MenuModeHelper.FilterByMode(menuList, model.MenuMode);
                    model.MenuList = menuList;
                }
            }

            model.ActiveMenuId = model.FindMenuId(model.CurrentPath);
            return View(model);
        }

        private string GetCurrentPath()
        {
            string path = ViewContext.HttpContext.Request.Path.Value;
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }
            return path.TrimEnd('/').ToLowerInvariant();
        }
    }

    /// <summary>
    /// 左侧菜单渲染模型
    /// </summary>
    public class SideMenuModel
    {
        public static readonly string OperatorInfoKey = "SideMenu_OperatorInfo";

        public OperatorInfo OperatorInfo { get; set; }

        public List<MenuEntity> MenuList { get; set; } = new List<MenuEntity>();

        /// <summary>
        /// 当前菜单模式：dev 开发模式 / manage 管理模式
        /// </summary>
        public string MenuMode { get; set; } = MenuModeHelper.DevMode;

        /// <summary>
        /// 当前请求路径，统一为小写、不带结尾斜杠，例如 /organizationmanage/user/userindex
        /// </summary>
        public string CurrentPath { get; set; } = string.Empty;

        /// <summary>
        /// 与当前路径匹配的菜单Id，用于高亮
        /// </summary>
        public long? ActiveMenuId { get; set; }

        public IEnumerable<MenuEntity> GetChildren(long? parentId)
        {
            return MenuList.Where(p => p.ParentId == parentId).OrderBy(p => p.MenuSort);
        }

        public bool IsActive(MenuEntity menu)
        {
            return menu != null && ActiveMenuId != null && menu.Id == ActiveMenuId;
        }

        public bool HasActiveChild(MenuEntity menu)
        {
            if (menu == null || ActiveMenuId == null)
            {
                return false;
            }
            return MenuList.Any(p => p.ParentId == menu.Id && p.Id == ActiveMenuId);
        }

        public bool IsHome
        {
            get
            {
                // 直接访问站点根地址或 Home 首页时，都要高亮“首页”菜单
                return string.IsNullOrEmpty(CurrentPath)
                    || CurrentPath == "/"
                    || string.Equals(CurrentPath, "/home", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CurrentPath, "/home/index", StringComparison.OrdinalIgnoreCase);
            }
        }

        public long? FindMenuId(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath))
            {
                return null;
            }
            foreach (MenuEntity menu in MenuList)
            {
                string menuPath = NormalizeUrl(menu.MenuUrl);
                if (!string.IsNullOrEmpty(menuPath) && string.Equals(menuPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return menu.Id;
                }
            }
            return null;
        }

        /// <summary>
        /// 把菜单里配置的地址（可能是 xxx/yyy/zzz、~/xxx/yyy/zzz、带查询串或外链）规范成可比较的路径
        /// </summary>
        public static string NormalizeUrl(string menuUrl)
        {
            if (string.IsNullOrEmpty(menuUrl))
            {
                return string.Empty;
            }
            string url = menuUrl.Trim();
            int queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                url = url.Substring(0, queryIndex);
            }
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                url = url.Substring(0, hashIndex);
            }
            url = url.TrimStart('~');
            if (url.Length == 0)
            {
                return string.Empty;
            }
            if (!url.StartsWith("/"))
            {
                url = "/" + url;
            }
            return url.TrimEnd('/');
        }
    }
}
