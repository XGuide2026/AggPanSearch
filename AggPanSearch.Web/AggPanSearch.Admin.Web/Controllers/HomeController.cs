using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Business.OrganizationManage;
using AggPanSearch.Business.SystemManage;
using AggPanSearch.Entity.SystemManage;
using AggPanSearch.Enum;
using AggPanSearch.IdGenerator;
using AggPanSearch.Model.Result;
using AggPanSearch.Util.Extension;
using AggPanSearch.Web.Code;
using AggPanSearch.Admin.Web.ViewComponents;
using AggPanSearch.Util.Model;
using AggPanSearch.Util;
using AggPanSearch.Entity.OrganizationManage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace AggPanSearch.Admin.Web.Controllers
{
    public class HomeController : BaseController
    {
        private UserBLL userBLL = new UserBLL();
        private LogLoginBLL logLoginBLL = new LogLoginBLL();

        #region 视图功能
        [HttpGet]
        [AuthorizeFilter]
        public IActionResult Index()
        {
            // 菜单与外壳统一由 _Index 布局渲染，首页直接显示欢迎页
            return View("Welcome");
        }

        [HttpGet]
        [AuthorizeFilter]
        public IActionResult Welcome()
        {
            return View();
        }

        [HttpGet]
        public IActionResult Login()
        {
            if (GlobalContext.SystemConfig.Demo)
            {
                ViewBag.UserName = "admin";
                ViewBag.Password = "123456";
            }
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> LoginOffJson()
        {
            OperatorInfo user = await Operator.Instance.Current();
            if (user != null)
            {
                #region 退出系统
                // 如果不允许同一个用户多次登录，当用户登出的时候，就不在线了
                if (!GlobalContext.SystemConfig.LoginMultiple)
                {
                    await userBLL.UpdateUser(new UserEntity { Id = user.UserId, IsOnline = 0 });
                }

                // 登出日志
                await logLoginBLL.SaveForm(new LogLoginEntity
                {
                    LogStatus = OperateStatusEnum.Success.ParseToInt(),
                    Remark = "退出系统",
                    IpAddress = NetHelper.Ip,
                    IpLocation = string.Empty,
                    Browser = NetHelper.Browser,
                    OS = NetHelper.GetOSVersion(),
                    ExtraRemark = NetHelper.UserAgent,
                    BaseCreatorId = user.UserId
                });

                Operator.Instance.RemoveCurrent();
                new CookieHelper().RemoveCookie("RememberMe");

                return Json(new TData { Tag = 1 });
                #endregion
            }
            else
            {
                throw new Exception("非法请求");
            }
        }

        [HttpGet]
        public IActionResult NoPermission()
        {
            return View();
        }

        [HttpGet]
        public IActionResult Error(string message)
        {
            ViewBag.Message = message;
            return View();
        }

        [HttpGet]
        public IActionResult Skin()
        {
            return View();
        }
        #endregion

        #region 获取数据
        [SupportedOSPlatform("windows")]
        public IActionResult GetCaptchaImage()
        {
            string sessionId = GlobalContext.ServiceProvider?.GetService<IHttpContextAccessor>().HttpContext.Session.Id;

            Tuple<string, int> captchaCode = CaptchaHelper.GetCaptchaCode();
            byte[] bytes = CaptchaHelper.CreateCaptchaImage(captchaCode.Item1);
            new SessionHelper().WriteSession("CaptchaCode", captchaCode.Item2);
            return File(bytes, @"image/jpeg");
        }
        #endregion

        #region 提交数据
        [HttpPost]
        public async Task<IActionResult> LoginJson(string userName, string password, string captchaCode)
        {
            TData obj = new TData();
            if (string.IsNullOrEmpty(captchaCode))
            {
                obj.Message = "验证码不能为空";
                return Json(obj);
            }
            if (captchaCode != new SessionHelper().GetSession("CaptchaCode").ParseToString())
            {
                obj.Message = "验证码错误，请重新输入";
                return Json(obj);
            }
            TData<UserEntity> userObj = await userBLL.CheckLogin(userName, password, (int)PlatformEnum.Web);
            if (userObj.Tag == 1)
            {
                await new UserBLL().UpdateUser(userObj.Data);
                await Operator.Instance.AddCurrent(userObj.Data.WebToken);
                // 每次登录都从开发模式开始，避免上次停留在管理模式
                Response.Cookies.Append(MenuModeHelper.CookieName, MenuModeHelper.DevMode, new CookieOptions
                {
                    Path = "/",
                    Expires = DateTimeOffset.Now.AddDays(30)
                });
            }

            string ip = NetHelper.Ip;
            string browser = NetHelper.Browser;
            string os = NetHelper.GetOSVersion();
            string userAgent = NetHelper.UserAgent;

            Action taskAction = async () =>
            {
                LogLoginEntity logLoginEntity = new LogLoginEntity
                {
                    LogStatus = userObj.Tag == 1 ? OperateStatusEnum.Success.ParseToInt() : OperateStatusEnum.Fail.ParseToInt(),
                    Remark = userObj.Message,
                    IpAddress = ip,
                    IpLocation = IpLocationHelper.GetIpLocation(ip),
                    Browser = browser,
                    OS = os,
                    ExtraRemark = userAgent,
                    BaseCreatorId = userObj.Data?.Id
                };

                // 让底层不用获取HttpContext
                logLoginEntity.BaseCreatorId = logLoginEntity.BaseCreatorId ?? 0;

                await logLoginBLL.SaveForm(logLoginEntity);
            };
            AsyncTaskHelper.StartTask(taskAction);

            obj.Tag = userObj.Tag;
            obj.Message = userObj.Message;
            return Json(obj);
        }
        #endregion
    }
}
