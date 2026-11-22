using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Util.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Business.PanManage;

namespace AggPanSearch.Admin.Web.Areas.PanManage.Controllers
{
    /// <summary>
    /// 描 述：切换主题（把用户前端导航栏的主题切换功能移植到后台）
    /// 数据存储：pan_conf 表的 theme / theme_switch 两个配置项
    /// </summary>
    [Area("PanManage")]
    public class PanConfThemeController : BaseController
    {
        private PanConfBLL panConfBLL = new PanConfBLL();

        // 与用户前端 _Layout.cshtml 中 [data-theme] 的取值保持一致
        private static readonly string[] ThemeNames = new string[] { "red", "blue", "green", "dark" };

        #region 视图功能
        [AuthorizeFilter("pan:pantheme:view")]
        public ActionResult PanConfThemeIndex()
        {
            return View();
        }
        #endregion

        #region 获取数据
        /// <summary>
        /// 获取当前站点主题配置
        /// </summary>
        [HttpGet]
        [AuthorizeFilter("pan:pantheme:view")]
        public async Task<ActionResult> GetThemeJson()
        {
            TData<Dictionary<string, string>> confObj = await panConfBLL.GetConfValues(new List<string> { "theme", "theme_switch" });
            Dictionary<string, string> values = confObj.Data ?? new Dictionary<string, string>();

            string theme;
            values.TryGetValue("theme", out theme);
            string themeSwitch;
            values.TryGetValue("theme_switch", out themeSwitch);

            TData<object> obj = new TData<object>();
            obj.Data = new
            {
                theme = NormalizeTheme(theme),
                themeSwitch = themeSwitch == "1" ? 1 : 0
            };
            obj.Tag = 1;
            return Json(obj);
        }
        #endregion

        #region 提交数据
        /// <summary>
        /// 保存站点主题配置
        /// </summary>
        [HttpPost]
        [AuthorizeFilter("pan:pantheme:edit")]
        public async Task<ActionResult> SaveThemeJson([FromBody] PanThemeParam param)
        {
            TData obj = new TData();
            if (param == null)
            {
                obj.Message = "参数错误";
                return Json(obj);
            }

            Dictionary<string, string> values = new Dictionary<string, string>();
            values["theme"] = NormalizeTheme(param.theme);
            values["theme_switch"] = param.themeSwitch == 1 ? "1" : "0";

            TData saveObj = await panConfBLL.SaveConfValues(values);
            obj.Tag = saveObj.Tag;
            obj.Message = saveObj.Tag == 1 ? "保存成功，用户前端刷新后生效" : saveObj.Message;
            return Json(obj);
        }
        #endregion

        #region 私有方法
        private string NormalizeTheme(string theme)
        {
            if (string.IsNullOrEmpty(theme))
            {
                return "red";
            }
            theme = theme.Trim().ToLower();
            foreach (string item in ThemeNames)
            {
                if (item == theme)
                {
                    return item;
                }
            }
            return "red";
        }
        #endregion
    }

    /// <summary>
    /// 主题保存参数
    /// </summary>
    public class PanThemeParam
    {
        public string theme { get; set; }

        public int themeSwitch { get; set; }
    }
}
