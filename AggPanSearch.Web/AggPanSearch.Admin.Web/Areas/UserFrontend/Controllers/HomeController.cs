using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Models;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Services;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Controllers
{
    [Area("UserFrontend")]
    public class HomeController : Controller
    {
        private readonly IUserFrontendService _service;

        public HomeController(IUserFrontendService service)
        {
            _service = service;
        }

        private SiteConfig Config => _service.GetConfig();

        /// <summary>首页（Razor 版）。</summary>
        [HttpGet]
        [Route("UserFrontend")]
        [Route("UserFrontend/Home")]
        [Route("UserFrontend/Home/Index")]
        [Route("uf")]
        public IActionResult Index()
        {
            var data = _service.GetIndexData();
            ViewData["Config"] = Config;
            ViewData["IsHome"] = true;
            ViewData["Title"] = Config.AppName;
            return View(data);
        }

        /// <summary>搜索结果页（Razor 版 list）。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/Search")]
        [Route("s/{keyword}.html")]
        public IActionResult Search(string keyword = "", int page = 1, int categoryId = 0)
        {
            if (string.IsNullOrWhiteSpace(keyword)) keyword = "";
            const int pageSize = 10;
            var model = _service.Search(keyword, page, pageSize, categoryId);

            ViewData["Config"] = Config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = keyword;
            ViewData["CategoryId"] = model.CategoryId > 0 ? model.CategoryId.ToString() : "";
            ViewData["FirstKey"] = model.FirstKey;
            return View(model);
        }

        /// <summary>资源详情页（Razor 版 detail）。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/Detail/{id}")]
        [Route("d/{id}.html")]
        public IActionResult Detail(string id)
        {
            var model = _service.GetDetail(id);
            ViewData["Config"] = Config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            return View(model);
        }

        /// <summary>资源分类页：左侧分类，右侧分类下资源列表。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/Category")]
        [Route("category")]
        public IActionResult Category(int page = 1, long categoryId = 0)
        {
            const int pageSize = 20;
            var model = _service.GetCategory(page, pageSize, categoryId);
            ViewData["Config"] = Config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            return View(model);
        }

        /// <summary>版权声明与侵权投诉页（前台页脚入口进入）。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/Copyright")]
        [Route("copyright")]
        public IActionResult Copyright()
        {
            ViewData["Config"] = Config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            ViewData["Title"] = "版权保护与侵权投诉 - " + Config.AppName;
            return View();
        }

        /// <summary>屏蔽词列表页（前台页脚入口进入）：公示当前参与搜索结果过滤的关键词。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/BannedWords")]
        [Route("banned_words")]
        public IActionResult BannedWords()
        {
            SiteConfig config = Config;
            // 与搜索接口的过滤规则保持一致：人工屏蔽词 + 版权关键词合并成一份，逗号/分号/换行分隔，去空去重
            var words = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string word in SplitKeywords(config.SearchBlockWords).Concat(SplitKeywords(config.CopyrightBlockKeywords)))
            {
                if (seen.Add(word))
                {
                    words.Add(word);
                }
            }

            ViewData["Config"] = config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            ViewData["Title"] = "屏蔽词列表 - " + config.AppName;
            return View(new BannedWordsPageModel
            {
                Words = words
            });
        }

        /// <summary>把逗号/分号/换行分隔的关键词串拆成列表（去空、忽略大小写去重、保留原顺序）。</summary>
        private static List<string> SplitKeywords(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in value.Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string word = part.Trim();
                if (word.Length > 0 && seen.Add(word))
                {
                    result.Add(word);
                }
            }
            return result;
        }

        /// <summary>错误页（Razor 版）。</summary>
        [HttpGet]
        [Route("UserFrontend/Home/ErrorPage")]
        public IActionResult ErrorPage()
        {
            ViewData["Config"] = Config;
            ViewData["IsHome"] = false;
            ViewData["Keyword"] = "";
            ViewData["Title"] = "出错了 - " + Config.AppName;
            return View();
        }
    }
}