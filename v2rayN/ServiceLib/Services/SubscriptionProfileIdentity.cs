namespace ServiceLib.Services;

internal static class SubscriptionProfileIdentity
{
    internal static void Preserve(List<ProfileItem> previous, List<ProfileItem> incoming)
    {
        var used = new HashSet<string>();
        var remap = new Dictionary<string, string>();
        // Match only identities that are unique on both sides of the same subscription.
        Match(p => (p.ConfigType, Address(p), p.Port, p.Username, p.Password, p.Remarks));
        Match(p => (p.ConfigType, Address(p), p.Port, p.Username, p.Password));
        Match(p => (p.ConfigType, Address(p), p.Port, p.Remarks));

        foreach (var item in incoming)
        {
            item.IndexId = remap.GetValueOrDefault(item.IndexId, item.IndexId);
            if (item.ConfigType.IsGroupType() && item.GetProtocolExtra() is { ChildItems.Length: > 0 } extra)
            {
                var ids = Utils.String2List(extra.ChildItems) ?? [];
                item.SetProtocolExtra(extra with { ChildItems = Utils.List2String(ids.Select(id => remap.GetValueOrDefault(id, id)).ToList()) });
            }
        }

        static string Address(ProfileItem p) => p.ConfigType is EConfigType.Outbound or EConfigType.Custom || p.ConfigType.IsGroupType()
            ? p.Remarks : p.Address.ToLowerInvariant();

        void Match<TKey>(Func<ProfileItem, TKey> key) where TKey : notnull
        {
            var oldGroups = previous.Where(p => !used.Contains(p.IndexId)).ToLookup(key);
            foreach (var group in incoming.Where(p => !remap.ContainsKey(p.IndexId)).GroupBy(key))
            {
                var old = oldGroups[group.Key];
                if (group.Count() != 1 || old.Count() != 1) continue;
                var id = old.First().IndexId;
                used.Add(id);
                remap.Add(group.First().IndexId, id);
            }
        }
    }
}
