using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Models;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Services;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Controllers
{
    /// <summary>
    /// 用户前台站点地图入口：XML 站点地图、爬虫指令文件、可视化站点地图页。
    /// 只做协议层工作（Content-Type、缓存头、域名解析），生成逻辑集中在 SitemapService。
    /// </summary>
    [Area("UserFrontend")]
    public class SitemapController : Controller
    {
        private readonly IUserFrontendService _frontendService;
        private readonly SitemapService _sitemapService;

        public SitemapController(IUserFrontendService frontendService, SitemapService sitemapService)
        {
            _frontendService = frontendService;
            _sitemapService = sitemapService;
        }

        private SiteConfig Config => _frontendService.GetConfig();

        /// <summary>XML 站点地图：栏目 + 分类 + 全量资源详情页。</summary>
        [HttpGet]
        [Route("sitemap.xml")]
        public IActionResult Xml()
        {
            string xml = _sitemapService.BuildSitemapXml(ResolveBaseUrl());
            // 内容按 30 分钟粒度刷新，允许中间层缓存，避免爬虫频繁触发全量查询
            Response.Headers["Cache-Control"] = "public, max-age=1800";
            return Content(xml, "application/xml", Encoding.UTF8);
        }

        /// <summary>爬虫指令文件：屏蔽搜索页与后台路径，声明站点地图地址。</summary>
        [HttpGet]
        [Route("robots.txt")]
        public IActionResult Robots()
        {
            string text = _sitemapService.BuildRobotsTxt(ResolveBaseUrl());
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Content(text, "text/plain", Encoding.UTF8);
        }

        /// <summary>可视化站点地图页：栏目导航 + 全部分类，作为内链枢纽。</summary>
        [HttpGet]
        [Route("sitemap")]
        public IActionResult Index()
        {
            SiteConfig config = Config;
            List<CategoryItem> categories = _sitemapService.GetCategoriesForPage();

            ViewData["Config"] = config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            ViewData["Title"] = "网站地图 - " + config.AppName;
            ViewData["SeoTitle"] = "网站地图 - " + config.AppName;
            ViewData["SeoKeywords"] = "网站地图,资源分类,网盘资源";
            ViewData["SeoDescription"] = "本站全部栏目与资源分类索引，共 " + categories.Count + " 个分类。";
            return View(categories);
        }

        /// <summary>
        /// 站点绝对地址：优先取后台配置的「网站网址」，未配置或非法时回退当前请求的协议与域名。
        /// </summary>
        private string ResolveBaseUrl()
        {
            string webAddress = Config?.WebAddress;
            if (!string.IsNullOrWhiteSpace(webAddress))
            {
                string trimmed = webAddress.Trim().TrimEnd('/');
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
                // 后台只填了域名（如 example.com）时补全协议
                return "https://" + trimmed.TrimStart('/');
            }
            return Request.Scheme + "://" + Request.Host.Value;
        }
    }
}
