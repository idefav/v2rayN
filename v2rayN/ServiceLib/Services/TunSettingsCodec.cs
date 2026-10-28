using System.Collections;
using System.Globalization;

namespace ServiceLib.Services;

public static class TunSettingsCodec
{
    public static Dictionary<string, string> Read(TunModeItem item)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in TunSettingsCatalog.Fields)
        {
            var target = field.Singbox ? (object?)item.Singbox : item;
            var value = target?.GetType().GetProperty(field.Property)!.GetValue(target);
            result[field.Key] = value switch
            {
                null => "",
                bool flag => flag ? "true" : "false",
                List<TunRulesetSource> sources => JsonUtils.Serialize(sources, false),
                string text => text,
                IEnumerable values => string.Join("\n", values.Cast<object>()),
                uint mark when field.Key.EndsWith("_mark", StringComparison.Ordinal) => $"0x{mark:x}",
                int number when number == 0 && field.Key == "mtu" => "",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
        }
        return result;
    }

    public static List<TunValidationIssue> Parse(TunModeItem original, IReadOnlyDictionary<string, string> values,
        TunPlatform platform, out TunModeItem draft)
    {
        draft = JsonUtils.DeepCopy(original)!;
        draft.Singbox ??= new();
        var issues = new List<TunValidationIssue>();
        foreach (var field in TunSettingsCatalog.Fields)
        {
            if (!values.TryGetValue(field.Key, out var raw)) continue;
            var target = field.Singbox ? (object)draft.Singbox : draft;
            var property = target.GetType().GetProperty(field.Property)!;
            var text = raw.Trim();
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            try
            {
                object? value;
                if (type == typeof(string)) value = text.Length == 0 ? null : text;
                else if (text.Length == 0)
                    value = Nullable.GetUnderlyingType(property.PropertyType) != null || !type.IsValueType ? null : Activator.CreateInstance(type);
                else if (type == typeof(bool)) value = bool.Parse(text);
                else if (type == typeof(int)) value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
                else if (type == typeof(uint)) value = ParseUnsigned(text);
                else if (type == typeof(ushort)) value = checked((ushort)ParseUnsigned(text));
                else if (type == typeof(List<string>)) value = Lines(text);
                else if (type == typeof(List<uint>)) value = Lines(text).Select(ParseUnsigned).ToList();
                else if (type == typeof(List<TunRulesetSource>)) value = JsonSerializer.Deserialize<List<TunRulesetSource>>(text) ?? [];
                else throw new NotSupportedException(property.Name);
                property.SetValue(target, value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException)
            {
                issues.Add(new(field.Key, TunSettingsText.T("格式或数值范围无效，请查看该选项说明。", "Invalid format or numeric range; see this option's help.")));
            }
        }
        if (issues.Count == 0) issues.AddRange(SingboxTunSettings.Validate(draft, platform));
        return issues;
    }

    public static List<string> Lines(string value) => value.Replace("\r", "").Split('\n',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();

    public static uint ParseUnsigned(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
