using System.Globalization;

namespace ServiceLib.Common;

/// <summary>
/// Extension methods for country code utilities
/// </summary>
public static class CountryExtension
{
    public static string? CountryDisplayName(string? code, string? fallback)
    {
        code = code?.ToUpperInvariant();
        var chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var name = fallback;
        if (code?.Length == 2)
            try { name = chinese ? new RegionInfo(code).DisplayName : new RegionInfo(code).EnglishName; }
            catch (ArgumentException) { }
        if (chinese)
            name = code switch
            {
                "US" => "美国", "JP" => "日本", "SG" => "新加坡", "HK" => "香港", "TW" => "台湾",
                "CN" => "中国", "DE" => "德国", "GB" => "英国", "FR" => "法国", "CA" => "加拿大",
                "AU" => "澳大利亚", "KR" => "韩国", "NL" => "荷兰", "IN" => "印度", "RU" => "俄罗斯",
                _ => name
            };
        return name ?? code;
    }

    /// <summary>
    /// Country code to emoji flag mapping for common countries
    /// </summary>
    private static readonly Dictionary<string, string> CountryEmojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Asia
        { "CN", "🇨🇳" }, // China
        { "HK", "🇭🇰" }, // Hong Kong
        { "TW", "🇹🇼" }, // Taiwan
        { "JP", "🇯🇵" }, // Japan
        { "SG", "🇸🇬" }, // Singapore
        { "KR", "🇰🇷" }, // South Korea
        { "TH", "🇹🇭" }, // Thailand
        { "VN", "🇻🇳" }, // Vietnam
        { "ID", "🇮🇩" }, // Indonesia
        { "PH", "🇵🇭" }, // Philippines
        { "MY", "🇲🇾" }, // Malaysia
        { "IN", "🇮🇳" }, // India
        { "PK", "🇵🇰" }, // Pakistan
        { "BD", "🇧🇩" }, // Bangladesh
        { "LK", "🇱🇰" }, // Sri Lanka
        { "KH", "🇰🇭" }, // Cambodia
        { "LA", "🇱🇦" }, // Laos
        { "MM", "🇲🇲" }, // Myanmar

        // Americas
        { "US", "🇺🇸" }, // United States
        { "CA", "🇨🇦" }, // Canada
        { "MX", "🇲🇽" }, // Mexico
        { "BR", "🇧🇷" }, // Brazil
        { "AR", "🇦🇷" }, // Argentina
        { "CL", "🇨🇱" }, // Chile
        { "CO", "🇨🇴" }, // Colombia

        // Europe
        { "GB", "🇬🇧" }, // United Kingdom
        { "DE", "🇩🇪" }, // Germany
        { "FR", "🇫🇷" }, // France
        { "IT", "🇮🇹" }, // Italy
        { "ES", "🇪🇸" }, // Spain
        { "RU", "🇷🇺" }, // Russia
        { "NL", "🇳🇱" }, // Netherlands
        { "CH", "🇨🇭" }, // Switzerland
        { "SE", "🇸🇪" }, // Sweden
        { "NO", "🇳🇴" }, // Norway
        { "DK", "🇩🇰" }, // Denmark
        { "FI", "🇫🇮" }, // Finland
        { "PL", "🇵🇱" }, // Poland
        { "CZ", "🇨🇿" }, // Czech Republic
        { "AT", "🇦🇹" }, // Austria
        { "GR", "🇬🇷" }, // Greece
        { "PT", "🇵🇹" }, // Portugal
        { "TR", "🇹🇷" }, // Turkey
        { "UA", "🇺🇦" }, // Ukraine
        { "RO", "🇷🇴" }, // Romania

        // Middle East & Central Asia
        { "AE", "🇦🇪" }, // United Arab Emirates
        { "SA", "🇸🇦" }, // Saudi Arabia
        { "IL", "🇮🇱" }, // Israel
        { "KZ", "🇰🇿" }, // Kazakhstan

        // Oceania
        { "AU", "🇦🇺" }, // Australia
        { "NZ", "🇳🇿" }, // New Zealand

        // Africa
        { "ZA", "🇿🇦" }, // South Africa
        { "EG", "🇪🇬" }, // Egypt
    };

    /// <summary>
    /// Converts country code to flag emoji using predefined mapping
    /// Example: "US" -> "🇺🇸", "CN" -> "🇨🇳"
    /// </summary>
    public static string? CountryToEmoji(this string? countryCode)
    {
        if (countryCode.IsNullOrEmpty())
        {
            return null;
        }

        return CountryEmojiMap.GetValueOrDefault(countryCode);
    }
}
