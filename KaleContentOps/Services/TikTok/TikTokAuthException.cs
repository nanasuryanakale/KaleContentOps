using System;

namespace KaleContentOps.Services.TikTok;

public class TikTokAuthException : Exception
{
    public int? ErrorCode { get; }
    public string? RequestId { get; }

    public TikTokAuthException(string message, int? errorCode = null, string? requestId = null)
        : base(message)
    {
        ErrorCode = errorCode;
        RequestId = requestId;
    }
}
