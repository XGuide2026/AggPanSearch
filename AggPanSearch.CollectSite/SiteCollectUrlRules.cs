namespace AggPanSearch.CollectSite;

/// <summary>
/// 采集相关的 URL 基本规则：协议判断、宽松比较、站点根地址、翻页去重键。
/// 引擎与业务层的地址校验统一走这里，避免多处实现导致口径不一致。
/// </summary>
public static class SiteCollectUrlRules
{
    /// <summary>是否为可采集的 http/https 绝对地址。</summary>
    public static bool IsHttpUrl(string? url)
    {
        return TryCreateHttpUri(url, out _);
    }

    /// <summary>校验并创建 http/https 绝对地址；不合法（含相对地址、其它协议）返回 false。</summary>
    public static bool TryCreateHttpUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    /// <summary>去掉地址开头的 http:// 或 https://，用于容忍协议差异的比较。</summary>
    public static string StripScheme(string? url)
    {
        string value = (url ?? string.Empty).Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return value.Substring("http://".Length);
        }
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value.Substring("https://".Length);
        }
        return value;
    }

    /// <summary>地址归一化：去协议、去 www. 前缀、去末尾斜杠，用于宽松比较。</summary>
    public static string NormalizeForCompare(string? url)
    {
        string value = StripScheme(url).TrimEnd('/');
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("www.".Length);
        }
        return value;
    }

    /// <summary>比较两个地址是否相同（忽略协议、www. 前缀、大小写与末尾斜杠）。</summary>
    public static bool IsSameUrl(string? url1, string? url2)
    {
        return string.Equals(NormalizeForCompare(url1), NormalizeForCompare(url2), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取站点根地址（scheme://host[:port]/），用作首屏 Referer。</summary>
    public static string GetSiteRoot(string? url)
    {
        return TryCreateHttpUri(url, out Uri uri)
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : string.Empty;
    }

    /// <summary>
    /// 翻页去重键：去掉 /page/1 与 fragment、统一小写，避免同一页被反复抓取形成死循环。
    /// </summary>
    public static string GetPageDedupKey(string? url)
    {
        if (!TryCreateHttpUri(url, out Uri uri))
        {
            return (url ?? string.Empty).Trim().ToLowerInvariant();
        }

        string path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (path.EndsWith("/page/1", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(0, path.Length - "/page/1".Length).TrimEnd('/');
        }
        if (path.Length == 0)
        {
            path = "/";
        }
        string normalized = path + uri.Query;
        if (normalized == "/?page=1")
        {
            normalized = "/";
        }
        return normalized.ToLowerInvariant();
    }
}
