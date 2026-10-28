using System.Collections;
using System.Text.Json;

namespace v2rayN.Desktop.Common;

/// <summary>Captures editable data only; commands, interactions and subscriptions are excluded.</summary>
public static class EditorSnapshot
{
    public static string Capture(object model)
    {
        if (model is TunSettingsViewModel tun) return tun.DraftSnapshot;
        if (model is BackupAndRestoreViewModel backup)
            return JsonSerializer.Serialize(new
            {
                Url = backup.SelectedSource.Url ?? "", UserName = backup.SelectedSource.UserName ?? "",
                Password = backup.SelectedSource.Password ?? "", DirName = backup.SelectedSource.DirName ?? ""
            });
        var values = new SortedDictionary<string, object?>();
        if (model is OptionSettingViewModel options)
            values["Sources"] = options.Sources.Select(r => new { r.Key, Value = r.Value ?? "" }).ToArray();
        foreach (var property in model.GetType().GetProperties())
        {
            if (model is OptionSettingViewModel { ManagesTunSettings: false } && property.Name.StartsWith("Tun", StringComparison.Ordinal)) continue;
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            if (model is OptionSettingViewModel && property.Name is nameof(OptionSettingViewModel.OnlySourcesChanged) or nameof(OptionSettingViewModel.InitialSection)) continue;
            if (model is DNSSettingViewModel && property.Name is nameof(DNSSettingViewModel.EgressError)
                or nameof(DNSSettingViewModel.NodeCapability)) continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (property.CanWrite && (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)))
                values[property.Name] = type == typeof(string) ? property.GetValue(model) ?? "" : property.GetValue(model);
            else if (property.GetValue(model) is IList list && property.Name is "DestOverride" or "CoreTypeItems" or "KeyEvents")
                values[property.Name] = list;
        }
        return JsonSerializer.Serialize(values);
    }
}
