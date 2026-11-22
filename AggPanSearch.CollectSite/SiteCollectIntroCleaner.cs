using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>
/// 资源简介清洗：采集入库前把简介里的「链接标签、网盘分享地址、微信引流文案」等冗余内容过滤掉。
/// 规则：
/// 1. 删除「链接：」「下载链接：」标签；
/// 2. 删除网盘分享链接（带 http(s):// 或裸域名写法，如 https://pan.quark.cn/s/e8aef260bed4），
///    网盘域名统一交给 <see cref="NetDiskDetector.GetLinkType"/> 判断，非网盘链接（官网、图片站等）保留；
/// 3. 删除「如果资源链接失效了，可以加微信：xxx，重新获取！」引流文案；
/// 4. 收尾清理删除后残留的空行、重复空白与行首尾多余标点。
/// 不访问网络、不读写文件，便于单元测试。
/// </summary>
public static class SiteCollectIntroCleaner
{
    /// <summary>需要整段删除的固定文案（按长度降序，避免短串先删导致长串漏删）。</summary>
    private static readonly string[] FixedTexts =
    {
        "如果资源链接失效了，可以加微信：hh1802770，重新获取！",
        "下载链接：",
        "链接："
    };

    /// <summary>
    /// 微信引流文案：兼容微信号变化、冒号/逗号前后空白（页面抓下来常是「加微信：  hh1802770  ，重新获取」）与中英文标点。
    /// </summary>
    private static readonly Regex WechatAdPattern = RegexCache.Get(
        @"如果资源链接失效了[，,]?\s*(?:可以)?加(?:微信|VX|vx|V信)\s*[：:]\s*[A-Za-z0-9_\-]+\s*[，,]?\s*重新获取[！!]?");

    /// <summary>页面把引流文案截断时的残句（如 meta 描述结尾的「如果资源链接失效了，可...」）。</summary>
    private static readonly Regex TruncatedAdPattern = RegexCache.Get(@"如果资源链接失效了[^\n]*");

    /// <summary>被上游解析截断后孤零零留在结尾的「如果」。</summary>
    private static readonly Regex DanglingIfPattern = RegexCache.Get(@"\s*如果\s*$");

    /// <summary>带协议或不带协议的域名链接（是否删除由网盘类型判断）。</summary>
    private static readonly Regex UrlPattern = RegexCache.GetIgnoreCase(
        @"(?:https?://)?(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}(?::\d+)?(?:/[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]*)?");

    /// <summary>清洗后只剩标点或空白的行，整行丢弃。</summary>
    private static readonly Regex SymbolOnlyLinePattern = RegexCache.Get(@"^[\s:：,，、;；|·\-—~_=*#\.]+$");

    private static readonly Regex LeadingSymbolPattern = RegexCache.Get(@"^[\s:：,，、;；|·\-—~]+");

    private static readonly Regex TrailingSymbolPattern = RegexCache.Get(@"[\s:：,，、;；|·\-—~]+$");

    private static readonly Regex MultiSpacePattern = RegexCache.Get(@"[ \t\u00A0]{2,}");

    /// <summary>
    /// 清洗资源简介；入参为空或清洗后为空时返回空字符串。
    /// </summary>
    public static string Clean(string? intro)
    {
        if (string.IsNullOrWhiteSpace(intro))
        {
            return string.Empty;
        }

        string text = intro
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');

        // 1) 引流文案先整句删除，避免「链接：」先删后句子被拆碎而残留
        text = WechatAdPattern.Replace(text, string.Empty);
        // 1.1) 页面截断导致只剩半句（「如果资源链接失效了，可...」）或只剩一个「如果」
        text = TruncatedAdPattern.Replace(text, string.Empty);
        text = DanglingIfPattern.Replace(text, string.Empty);

        // 2) 网盘链接：是网盘的整段删除，非网盘的原样保留
        text = UrlPattern.Replace(text, match => IsNetDiskUrl(match.Value) ? string.Empty : match.Value);

        // 3) 固定文案标签
        foreach (string fixedText in FixedTexts)
        {
            text = text.Replace(fixedText, string.Empty);
        }

        return Cleanup(text);
    }

    /// <summary>链接是否属于网盘（含磁力/电驴），非网盘返回 false。</summary>
    private static bool IsNetDiskUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        string linkType = NetDiskDetector.GetLinkType(url.Trim());
        return !string.Equals(linkType, CloudTypes.Others, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>收尾：去掉空行、只剩标点的行、行首尾多余标点，并压缩连续空白。</summary>
    private static string Cleanup(string text)
    {
        string[] lines = text.Split('\n');
        List<string> kept = new List<string>();
        foreach (string rawLine in lines)
        {
            string line = MultiSpacePattern.Replace(rawLine, " ").Trim();
            if (line.Length == 0 || SymbolOnlyLinePattern.IsMatch(line))
            {
                continue;
            }
            line = TrailingSymbolPattern.Replace(line, string.Empty);
            line = LeadingSymbolPattern.Replace(line, string.Empty).Trim();
            if (line.Length == 0 || SymbolOnlyLinePattern.IsMatch(line))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }
}
