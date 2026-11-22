using System.Collections.Generic;

namespace AggPanSearch.Admin.Web.Areas.UserFrontend.Models
{
    /// <summary>站点配置（替代原 ThinkPHP {$config.xxx}）。对应二开版 site_conf 表。</summary>
    public class SiteConfig
    {
        public string HomeBg { get; set; } = "https://static.xinyuepan.com/home_bg.png";
        // 站点主题（pan_conf.theme）：red/blue/green/dark，由后台"网盘管理-切换主题"统一设置
        public string Theme { get; set; } = "red";
        // 是否允许访客在前台导航栏自行切换主题（pan_conf.theme_switch）：1 允许 0 关闭
        public int ThemeSwitch { get; set; }
        public string Logo { get; set; } = "";
        public string AppName { get; set; } = "妙搜云";
        public int AppNameHide { get; set; }
        public string AppSubname { get; set; } = "专业网盘资源搜索分享平台";
        // 网站网址（pan_conf.web_address），前台页脚展示
        public string WebAddress { get; set; } = "";
        public string AppIcon { get; set; } = "";
        public string AppLinks { get; set; } = "";
        public string AppDemand { get; set; } = "";
        public string HomeColor { get; set; } = "#3e3e3e";
        public string HomeTheme { get; set; } = "#ff6b6b";
        public string HomeBackground { get; set; } = "#fafafa";
        public string OtherBackground { get; set; } = "#ffffff";
        public string HomeCss { get; set; } = "";
        public string HomeNewImg { get; set; } = "";
        public string FooterDec { get; set; } = "本站仅为资源索引，不存储、不传播任何文件，仅供学习交流使用。";
        public string FooterCopyright { get; set; } = "© 2026 妙搜云 · ";
        public string SearchBg { get; set; } = "";
        public string SearchTips { get; set; } = "未找到，可换个关键词尝试哦~";
        // 搜索屏蔽词（pan_conf.search_block_words）：以逗号分隔，搜索结果标题/描述命中的条目会被过滤
        public string SearchBlockWords { get; set; } = "";
        // 版权屏蔽关键词（pan_conf.copyright_block_keywords）：由版权投诉审核通过后自动生成，与上者一起参与过滤
        public string CopyrightBlockKeywords { get; set; } = "";
        public int PcType { get; set; }
        public int IsQuan { get; set; }                      // 1 启用全网搜
        public int HomeNew { get; set; }                     // 已失效：原控制首页"热门搜索/最近更新"区块，区块已移除，此开关保留兼容后台配置
        public string SeoStatistics { get; set; } = "";
        // 二开版广告位
        public string AdHomeTop { get; set; } = "";
        public string AdHomeBottom { get; set; } = "";
        public string AdListTop { get; set; } = "";
        public string AdListBottom { get; set; } = "";
        public string AdDetailTop { get; set; } = "";
        public string AdDetailBottom { get; set; } = "";
        public string AdFooter { get; set; } = "";
        // 进站弹窗广告（pan_conf：ad_popup_*）：1 开启，会话内每个访客只弹一次
        public int AdPopupEnable { get; set; }
        public string AdPopupTitle { get; set; } = "";
        public string AdPopupImage { get; set; } = "";
        public string AdPopupContent { get; set; } = "";
        public string AdPopupLink { get; set; } = "";
        public string AdPopupBtn { get; set; } = "知道了";
        // 首页右侧悬浮方块（pan_conf 的 conf_type=13「联系方式」分类）：两组内容各自由独立开关控制
        public int ContactQrcode1Enable { get; set; }
        public string ContactQrcode1Image { get; set; } = "";
        public string ContactQrcode1Title { get; set; } = "";
        public string ContactQrcode1Desc { get; set; } = "";
        public int ContactQrcode2Enable { get; set; }
        public string ContactQrcode2Image { get; set; } = "";
        public string ContactQrcode2Title { get; set; } = "";
        public string ContactQrcode2Desc { get; set; } = "";
        // 悬浮方块底部的「联系链接」：pan_conf 的 contact_linkN_enable / contact_linkN_url / contact_linkN_icon（N 从 1 起，可配置任意多个）
        public List<ContactLinkItem> ContactLinks { get; set; } = new List<ContactLinkItem>();
        // 公众号引流弹窗（pan_conf：traffic_wxchannel_search）
        // 0 不弹窗 / 1 点击「搜索资源」强制弹窗 / 2 查看详情页资源链接强制弹窗
        public int TrafficWxchannelSearch { get; set; }
        // 公众号引流弹窗（pan_conf：traffic_wxchannel_viewsource）
        // 0 不弹窗 / 1 点击资源详情链接先弹窗、验证通过后跳转 / 2 详情页隐藏资源地址，点「查看资源」弹窗后显示
        public int TrafficWxchannelViewsource { get; set; }
        // 公众号名称（pan_conf：traffic_wxchannel_name）：展示在弹窗操作步骤里「或者搜索公众号：xxx」
        public string TrafficWxchannelName { get; set; } = "程序员软件库";
        // 公众号二维码（pan_conf：qr_wxchannel，二维码设置里的图片上传项，值为相对路径）
        public string QrWxchannel { get; set; } = "";
        // 公众号引流激活码（pan_conf：traffic_wxchannel_code）：支持配置多个，用「、」/逗号/分号/空格分隔，
        // 用户输入命中任意一个即验证通过；留空表示不校验激活码，关注后点按钮即可继续
        public string TrafficWxchannelCode { get; set; } = "";
        // 网盘类型映射：0夸克 1阿里 2百度 3UC 4迅雷 5-123盘 6-115 7天翼 8移动 9磁力 10光鸭
        public List<string[]> PanTypeMap { get; set; }
        public string PanTypeJson { get; set; } = "{}";
        public string PanTypeKeysJson { get; set; } = "[]";
        // 搜索统计（今日概览）
        public string TodayFormatted { get; set; } = "0";
        public string TotalFormatted { get; set; } = "3.6K";
        // 首页统计卡片"搜索耗时"：pan_search_log.cost_ms 平均耗时（无数据时为兜底值）
        public string LoadTime { get; set; } = "0.26s";
        // 首页统计三项的显示开关（pan_conf.conf_type=3「前端设置」）：1 显示 / 0 隐藏，其它脏值按显示处理
        // stat_show_panel 为总开关：为 0 时三项一律隐藏；为 1 时再分别看下面三个单项开关
        public int StatShowPanel { get; set; } = 1;      // stat_show_panel  显示今日统计面板（总开关）
        public int StatShowToday { get; set; } = 1;      // stat_show_today  今日搜索
        public int StatShowTotal { get; set; } = 1;      // stat_show_total  总搜索量
        public int StatShowAvgcost { get; set; } = 1;    // stat_show_avgcost 搜索耗时（注意属性名是 Avgcost，与下划线转帕斯卡的规则一致）
        // 搜索结果是否显示"来源"标签（pan_conf：is_show_source）：1 显示 / 0 隐藏
        public int IsShowSource { get; set; } = 1;
    }

    /// <summary>联系链接（首页右侧悬浮方块底部的图标按钮）：对应 pan_conf 的 contact_linkN_url / contact_linkN_icon。</summary>
    public class ContactLinkItem
    {
        /// <summary>跳转地址（仅放行 http/https/mailto 与站内相对路径）</summary>
        public string Url { get; set; } = "";

        /// <summary>图标：内联 SVG 源码（后台控件为多行文本，直接粘贴 &lt;svg ...&gt;），也兼容图片地址</summary>
        public string Icon { get; set; } = "";

        /// <summary>Icon 是否为内联 SVG 源码（true 时按 HTML 原样输出，false 时按图片地址渲染）</summary>
        public bool IsSvg { get; set; }
    }

    /// <summary>屏蔽词列表页模型（前台 /banned_words）：向访客公示当前参与搜索过滤的关键词。</summary>
    public class BannedWordsPageModel
    {
        /// <summary>全部屏蔽关键词（人工维护 + 版权保护，已去重）</summary>
        public List<string> Words { get; set; } = new List<string>();
    }

    /// <summary>站内一条资源记录。</summary>
    public class ResourceItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Code { get; set; } = "";
        public string Times { get; set; } = "";
        public string Src { get; set; } = "";
        public string Description { get; set; } = "";        // 副标题/描述（description）
        public int PageViews { get; set; }                   // 浏览量（page_views）
        public string ImageUrl { get; set; } = "";           // 资源预览图，为空时前台显示占位图
        public string VodContent { get; set; } = "";         // 资源介绍（vod_content）
        public int? SourceCategoryId { get; set; }           // 分类ID（source_category_id）
        public string CategoryName { get; set; } = "";       // 分类名称，由 source_category_id 映射而来
        public int IsType { get; set; }                      // 网盘类型键
    }

    /// <summary>前台分类项（来源 pan_source_category 表）。</summary>
    public class CategoryItem
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        /// <summary>该分类下的资源数量（pan_source 按 source_category_id 分组统计）</summary>
        public int Count { get; set; }
    }

    /// <summary>排行榜分类（首页右侧/侧栏）。</summary>
    public class RankBlock
    {
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        public List<ResourceItem> List { get; set; } = new List<ResourceItem>();
    }

    /// <summary>详情。</summary>
    public class ResourceDetail : ResourceItem
    {
        public string VodPic { get; set; } = "";
        public List<string> VodPics { get; set; } = new List<string>();   // 轮播图片（vod_pic 多图拆分，首张与 VodPic 一致）
        public List<ResourceItem> SameList { get; set; } = new List<ResourceItem>();
    }

    /// <summary>搜索列表页模型（二开版 list）。</summary>
    public class SearchPageModel
    {
        public string Keyword { get; set; } = "";
        public int PageNo { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public int TotalResult { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        public int blocked { get; set; }
        public List<ResourceItem> Items { get; set; } = new List<ResourceItem>();
        public List<ResourceItem> DisplayList { get; set; } = new List<ResourceItem>(); // 网盘筛选
        public List<RankBlock> HotList { get; set; } = new List<RankBlock>();
        public List<string> RankList { get; set; } = new List<string>();
        public string FirstKey { get; set; } = "0";
        public Dictionary<string, string> Category { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>首页模型。</summary>
    public class IndexPageModel
    {
        public List<ResourceItem> NewList { get; set; } = new List<ResourceItem>();
        // 资源分类（与"资源分类"页同一数据源 pan_source_category）
        public List<CategoryItem> Categories { get; set; } = new List<CategoryItem>();
        // 分类资源列表（与"资源分类"页右侧列表同一数据源 pan_source）
        public List<ResourceItem> CategoryList { get; set; } = new List<ResourceItem>();
        public List<RankBlock> HotList { get; set; } = new List<RankBlock>();
        public List<string> RankList { get; set; } = new List<string>();
    }

    /// <summary>资源分类页模型，左侧分类 + 右侧资源列表。</summary>
    public class CategoryPageModel
    {
        public List<ResourceItem> Items { get; set; } = new List<ResourceItem>();
        public int PageNo { get; set; } = 1;
        public int PageSize { get; set; } = 20;
        public int TotalResult { get; set; }
        public List<CategoryItem> Categories { get; set; } = new List<CategoryItem>();
        // 分类主键是 bigint，后台新增的分类是雪花 ID（19 位），用 int 会溢出导致 URL 传参失效
        public long CurrentCategoryId { get; set; }
        public string CurrentCategoryName { get; set; } = "";
        public List<RankBlock> HotList { get; set; } = new List<RankBlock>();
    }

    /// <summary>详情页模型。</summary>
    public class DetailPageModel
    {
        public ResourceDetail Detail { get; set; } = new ResourceDetail();
        public List<ResourceItem> SameList { get; set; } = new List<ResourceItem>();
        public List<RankBlock> HotList { get; set; } = new List<RankBlock>();
        public List<string> RankList { get; set; } = new List<string>();
        public int PcType { get; set; }
    }
}