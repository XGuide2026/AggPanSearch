using System.Net;
using System.Text.RegularExpressions;
using AggPanSearch.CollectSite.NetDisk;

namespace AggPanSearch.CollectSite;

/// <summary>
/// WTS 安全盾（响应头 <c>Server: wts</c>）的 JS 校验令牌解析。
/// 被拦截时站点返回 403 校验页，页面内联脚本写入 <c>_wtsjsk</c> Cookie 后 reload；
/// 服务端只校验该 Cookie 是否随请求带回，因此无需执行 JS 或浏览器内核，
/// 只要把页面里的令牌取出来写入 Cookie 再重放原地址即可通过。
/// 纯函数，不触网、不读写文件，便于单测与各处抓取器复用。
/// </summary>
public static class WtsChallenge
{
    /// <summary>校验令牌 Cookie 名。</summary>
    public const string CookieName = "_wtsjsk";

    /// <summary>解析上限：校验页通常不足 2KB，超出该长度不再扫描，避免异常响应体拖慢处理。</summary>
    private const int MaxScanLength = 64 * 1024;

    /// <summary>令牌 Cookie 有效期：站点 _wtsexp 约 10 分钟，这里留足余量；过期后由下一次 403 重新刷新。</summary>
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromMinutes(30);

    /// <summary>页面内联脚本形如 <c>document.cookie = "_wtsjsk=0ddfd901...; path=/ ";</c>。</summary>
    private static readonly Regex TokenPattern = RegexCache.GetIgnoreCase(
        "_wtsjsk\\s*=\\s*[\"']?(?<t>[0-9a-f]{16,64})");

    /// <summary>判断响应体是否为 WTS 校验页（即能解析出令牌）。</summary>
    public static bool IsChallengeBody(string? body)
    {
        return TryExtractToken(body, out _);
    }

    /// <summary>从 403 校验页文本中提取 <c>_wtsjsk</c> 令牌，未命中返回 false。</summary>
    public static bool TryExtractToken(string? body, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        string text = body.Length > MaxScanLength ? body.Substring(0, MaxScanLength) : body;
        // 快速排除：正常页面几乎不会出现该特征串，避免每个失败响应都跑正则
        if (text.IndexOf(CookieName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        Match match = TokenPattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        token = match.Groups["t"].Value;
        return token.Length > 0;
    }

    /// <summary>按目标地址构造校验令牌 Cookie：仅对站点主机生效、路径为根。</summary>
    public static Cookie CreateCookie(Uri target, string token)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new Cookie(CookieName, token, "/", target.Host)
        {
            Expires = DateTime.Now.Add(CookieLifetime),
            HttpOnly = false,
            // 站点多为 http，Secure 固定 false 才能回传；https 站点同样适用
            Secure = false,
        };
    }
}
