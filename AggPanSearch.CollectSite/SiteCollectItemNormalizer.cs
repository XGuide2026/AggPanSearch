using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>条目归一化结果：保留下来的条目与跳过的条数（空链接、超长、非网盘、重复）。</summary>
public sealed class SiteCollectItemNormalizeResult
{
    public List<CollectedItem> Items { get; } = new List<CollectedItem>();

    public int Skipped { get; set; }
}

/// <summary>
/// 采集条目归一化：链接标准化 → 补提取码 → 跨页去重 → 类型过滤 → 字段裁剪。
/// 不访问网络、不读写文件，便于单元测试。
/// </summary>
public static class SiteCollectItemNormalizer
{
    public const int MaxNameLength = 500;

    public const int MaxIntroLength = 2000;

    public const int MaxCategoryLength = 100;

    public const int MaxUrlLength = 1000;

    public const int MaxImageLength = 1000;

    private static readonly Regex PwdParamPattern = RegexCache.GetIgnoreCase(@"[?&](?:pwd|password|code)=");

    private static readonly Regex WhitespacePattern = RegexCache.GetIgnoreCase(@"\s+");

    /// <summary>
    /// 归一化一批条目：空链接、超长链接、非网盘类型与重复链接计入 <see cref="SiteCollectItemNormalizeResult.Skipped"/>，
    /// 其余按字段上限裁剪后返回，最多保留 <paramref name="maxItems"/> 条。
    /// </summary>
    public static SiteCollectItemNormalizeResult Normalize(
        IEnumerable<CollectedItem>? items,
        ISet<string> seen,
        int maxItems)
    {
        SiteCollectItemNormalizeResult normalized = new SiteCollectItemNormalizeResult();
        if (items == null || maxItems <= 0)
        {
            return normalized;
        }

        foreach (CollectedItem item in items)
        {
            if (normalized.Items.Count >= maxItems)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Url))
            {
                normalized.Skipped++;
                continue;
            }

            string url = NetDiskDetector.NormalizeUrl(item.Url);
            if (url.Length == 0 || url.Length > MaxUrlLength)
            {
                normalized.Skipped++;
                continue;
            }

            string cloudType = string.IsNullOrWhiteSpace(item.CloudType)
                ? NetDiskDetector.GetLinkType(url)
                : item.CloudType;
            if (string.Equals(cloudType, CloudTypes.Others, StringComparison.OrdinalIgnoreCase))
            {
                normalized.Skipped++;
                continue;
            }

            url = AppendPassword(url, item.Password);
            if (!seen.Add(url))
            {
                normalized.Skipped++;
                continue;
            }

            string name = SiteCollectHtmlUtil.Truncate(NormalizeName(item.Name), MaxNameLength);
            // 多图按条累加拼接，超长时整条丢弃，避免出现半截地址
            string imageUrls = SiteCollectHtmlUtil.JoinImages(
                item.ImageUrls.Count > 0 ? item.ImageUrls : SiteCollectHtmlUtil.SplitImages(item.ImageUrl),
                MaxImageLength);
            normalized.Items.Add(new CollectedItem
            {
                Name = name.Length > 0 ? name : "未命名资源",
                Intro = SiteCollectHtmlUtil.Truncate(SiteCollectIntroCleaner.Clean(item.Intro), MaxIntroLength),
                Url = url,
                Password = item.Password,
                Category = SiteCollectHtmlUtil.Truncate(item.Category, MaxCategoryLength),
                ImageUrl = imageUrls,
                ImageUrls = SiteCollectHtmlUtil.SplitImages(imageUrls),
                DetailUrl = item.DetailUrl,
                CloudType = cloudType,
            });
        }

        return normalized;
    }

    /// <summary>链接缺少提取码参数时补上 pwd，已有参数则不覆盖。</summary>
    public static string AppendPassword(string url, string? password)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(password) || PwdParamPattern.IsMatch(url))
        {
            return url;
        }
        string separator = url.Contains('?') ? "&" : "?";
        return url + separator + "pwd=" + Uri.EscapeDataString(password.Trim());
    }

    /// <summary>名称归一化：连续空白折叠为单个空格并去首尾空白。</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }
        return WhitespacePattern.Replace(name, " ").Trim();
    }
}
