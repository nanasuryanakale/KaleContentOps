namespace KaleContentOps.Models;

/// <summary>
/// Classifier for automated ContentType determination based on business rules.
/// Priority:
/// 1. AUTO_GMV_LIVE - from live stream source (HasCommerce=true and IsLive=true)
/// 2. KK - video with product link (ProductsJson has data)
/// 3. NON-KK - regular video without products
/// </summary>
public static class ContentTypeClassifier
{
    public const int KK_TYPE_ID = 1;
    public const int NON_KK_TYPE_ID = 2;
    public const int AUTO_GMV_LIVE_TYPE_ID = 3;

    public const string KK_CODE = "KK";
    public const string NON_KK_CODE = "NON_KK";
    public const string AUTO_GMV_LIVE_CODE = "AUTO_GMV_LIVE";

    /// <summary>
    /// Determine ContentTypeId based on business rules.
    /// Uses ContentLog indicators and latest ContentMetric data.
    /// </summary>
    /// <param name="isLive">Flag indicating content originates from live stream</param>
    /// <param name="hasCommerce">Flag indicating content has commerce capability</param>
    /// <param name="productsJson">JSON array of products from latest ContentMetric</param>
    /// <returns>ContentTypeId (1=KK, 2=NON_KK, 3=AUTO_GMV_LIVE), or null if cannot determine</returns>
    public static int? ClassifyContentType(bool? isLive, bool? hasCommerce, string? productsJson)
    {
        // Priority 1: AUTO GMV LIVE
        if (isLive == true && hasCommerce == true)
        {
            return AUTO_GMV_LIVE_TYPE_ID;
        }

        // Priority 2: KK - has product links
        if (!string.IsNullOrWhiteSpace(productsJson) && HasProducts(productsJson))
        {
            return KK_TYPE_ID;
        }

        // Priority 3: NON-KK - regular video without products
        // For regular videos without explicit LIVE marker, assume NON-KK
        if (isLive != true && hasCommerce != true)
        {
            return NON_KK_TYPE_ID;
        }

        // If commerce is true but not marked as live, classify as KK (has commerce capability)
        if (hasCommerce == true)
        {
            return KK_TYPE_ID;
        }

        // Default: NON-KK for videos without clear commerce or live indicators
        return NON_KK_TYPE_ID;
    }

    /// <summary>
    /// Check if productsJson contains valid product data.
    /// </summary>
    private static bool HasProducts(string? productsJson)
    {
        if (string.IsNullOrWhiteSpace(productsJson))
        {
            return false;
        }

        try
        {
            var trimmed = productsJson.Trim();
            if (trimmed == "[]" || trimmed == "{}")
            {
                return false;
            }

            // Simple check: if JSON starts with [ and has content, consider it as having products
            if (trimmed.StartsWith("[") && trimmed.Length > 2)
            {
                return true;
            }
        }
        catch
        {
            // If JSON parsing fails, assume no products
            return false;
        }

        return false;
    }

    /// <summary>
    /// Get ContentType code string for a given ID.
    /// </summary>
    public static string? GetCodeForId(int? typeId) => typeId switch
    {
        KK_TYPE_ID => KK_CODE,
        NON_KK_TYPE_ID => NON_KK_CODE,
        AUTO_GMV_LIVE_TYPE_ID => AUTO_GMV_LIVE_CODE,
        _ => null
    };
}
