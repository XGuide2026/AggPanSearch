namespace AggPanSearch.Config;

/// <summary>
/// AggPanSearch 网盘搜索核心配置。
/// 对应原版 Go 项目中的环境变量配置（config）。
/// </summary>
public sealed class AggPanSearchConfig
{
    /// <summary>默认搜索的 TG 频道（多个用英文逗号分隔）。来自原版 PanSou 官方维护列表，并经可用性探测过滤。</summary>
    public string Channels { get; set; } = DefaultChannels;

    /// <summary>SOCKS5/HTTP 代理地址（如 socks5://127.0.0.1:1080 或 http://127.0.0.1:7890）。</summary>
    public string? Proxy { get; set; }

    /// <summary>要启用的插件（逗号分隔）。为空则使用全部内置插件。</summary>
    public string EnabledPlugins { get; set; } = "";

    /// <summary>并发搜索数，0 表示自动计算。</summary>
    public int Concurrency { get; set; }

    /// <summary>插件超时时间（秒）。</summary>
    public double PluginTimeoutSeconds { get; set; } = 30;

    /// <summary>异步响应的最快响应超时（秒）。窗口到期后返回已收集结果，不等最慢任务。</summary>
    public double AsyncResponseTimeoutSeconds { get; set; } = 1.5;

    /// <summary>TG 单频道请求超时（秒）。</summary>
    public double TgRequestTimeoutSeconds { get; set; } = 15;

    /// <summary>缓存有效期（分钟）。</summary>
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>缓存最大容量（MB）。</summary>
    public int CacheMaxSizeMb { get; set; } = 100;

    /// <summary>缓存文件路径（二级磁盘缓存目录）。</summary>
    public string CachePath { get; set; } = "./cache";

    /// <summary>是否启用异步插件搜索。</summary>
    public bool AsyncPluginEnabled { get; set; } = true;

    /// <summary>日志回调（可挂接任意日志框架，默认空实现）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>原生 ASCII 正则是否启用（true 用 System.Text.RegularExpressions compiled，性能更优）。</summary>
    public bool UseCompiledRegex { get; set; } = true;

    /// <summary>构造基于回调的默认日志记录器。</summary>
    public void LogDebug(string message) => Log?.Invoke(message);

    /// <summary>解析频道名称列表（去空、去空白）。</summary>
    public IReadOnlyList<string> GetChannelList()
        => SplitCommaList(Channels);

    /// <summary>解析启用插件列表（去空、去空白）。为空表示全部内置插件。</summary>
    public IReadOnlyList<string> GetEnabledPluginList()
        => SplitCommaList(EnabledPlugins);

    private static IReadOnlyList<string> SplitCommaList(string value)
        => (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 默认 TG 频道列表：取自原版 PanSou 官方维护列表（GitHub issue#4），
    /// 并用多关键词（影视/电影/2024/夸克/百度/电视剧）对 t.me/s/{channel}?q= 做真实搜索探测过滤。
    /// 剔除搜索不到任何网盘链接的失效/停更/无关频道（2026-10-31 探测，共 108 进 90 出）。
    /// </summary>
    private const string DefaultChannels =
        "tgsearchers7,Aliyun_4K_Movies,yunpanx,yp123pan,yunpanxunlei,tianyifc,peccxinpd," +
        "gotopan,PanjClub,baicaoZY,MCPH03,bdwpzhpd,Q66Share,ucwpzy,shareAliyun," +
        "Quark_Movies,XiangxiuNBB,ucquark,xx123pan,yingshifenxiang123,zyfb123,Lsp115,taoxgzy," +
        "Channel_Shares_115,vip115hot,wp123zy,yunpanuc,yydf_hzl,leoziyuan," +
        "yoyokuakeduanju,TG654TG,yeqingjie_GJG666,movielover8888_film3,Baidu_netdisk," +
        "D_wusun,FLMdongtianfudi,KaiPanshare,rjyxfx,newproductsourcing," +
        "QuarkFree,yunpanNB,kkdj001,xxzlzn,pxyunpanxunlei,jxwpzy,kuakedongman,xiangnikanj," +
        "solidsexydoll,guoman4K,zdqxm,kduanju,CBduanju,SharePanFilms,dzsgx," +
        "BooksRealm,douerpan,Netdisk_Movies,yunpanquark,jzmm_123pan,wpan8,mqte5," +
        "yy80986098," +
        "guangyapindao,quark_res,dianying4k,tgbokee,ucshare,gokuapan,WFYSFX03," +
        "fcij5,xvth5,xuexiziliaobaibaoku,phzvip,jdbigdiscount,youxigs," +
        "zhoulanziyuan,seedhub_pro,jnjy_5,xxziliao,wpzyk,jpnd5,XunLeiPinDao," +
        "WPpindao,kuyupan,djya5,zh_vip,gdsharing,alyp_17362," +
        "rbzhwpzy,Panzi88com";
}