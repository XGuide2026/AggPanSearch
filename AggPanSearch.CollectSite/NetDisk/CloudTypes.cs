namespace AggPanSearch.CollectSite.NetDisk;

/// <summary>
/// 网盘类型常量。对应 Go 项目支持的网盘类型。
/// </summary>
public static class CloudTypes
{
    public const string Baidu = "baidu";
    public const string Quark = "quark";
    public const string Aliyun = "aliyun";
    public const string Guangya = "guangya";
    public const string Tianyi = "tianyi";
    public const string Uc = "uc";
    public const string Mobile = "mobile";
    public const string Pan115 = "115";
    public const string Pikpak = "pikpak";
    public const string Xunlei = "xunlei";
    public const string Pan123 = "123";
    public const string Magnet = "magnet";
    public const string Ed2k = "ed2k";
    public const string Others = "others";

    // 排序标签：用于最终按网盘类型分组时的展示顺序
    /// <summary>网盘类型展示优先级（越小越靠前）。</summary>
    public static IReadOnlyList<string> DisplayOrder { get; } = new[]
    {
        Baidu, Quark, Aliyun, Guangya, Tianyi, Uc, Mobile, Pan115, Pikpak, Xunlei, Pan123, Magnet, Ed2k, Others,
    };
}
