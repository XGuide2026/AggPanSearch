namespace AggPanSearch.AI;

/// <summary>
/// AI 调用失败的分类，调用方据此决定提示文案与是否可重试。
/// 新增厂商时不需要扩展该枚举，只有出现全新失败语义时才追加。
/// </summary>
public enum AiErrorKind
{
    /// <summary>未归类的失败。</summary>
    Unknown = 0,

    /// <summary>配置缺失或配置非法（未填 Key、厂商不存在等），重试无意义。</summary>
    InvalidConfig,

    /// <summary>鉴权失败（HTTP 401），Key 无效或已失效。</summary>
    Unauthorized,

    /// <summary>账户余额不足（HTTP 402）。</summary>
    InsufficientBalance,

    /// <summary>模型不存在或已下线（HTTP 400 且响应指明模型无效）。</summary>
    ModelNotFound,

    /// <summary>请求参数被拒绝（其它 HTTP 400）。</summary>
    BadRequest,

    /// <summary>触发限流（HTTP 429）。</summary>
    RateLimited,

    /// <summary>上游服务端异常（HTTP 5xx）。</summary>
    UpstreamError,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>网络不可达（DNS、连接被拒、代理异常等）。</summary>
    Network
}
