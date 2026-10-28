using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

public class UpdateSourcesTests
{
    [Test]
    [Arguments(" idefav/sing-box ")]
    [Arguments("https://github.com/idefav/sing-box")]
    [Arguments("https://github.com/idefav/sing-box/releases/")]
    public async Task NormalizesRepository(string value) =>
        await UpdateSources.NormalizeRepository(value).Should().BeEqualTo("idefav/sing-box");

    [Test]
    [Arguments("https://example.com/idefav/sing-box")]
    [Arguments("https://github.com/idefav/sing-box?token=foo")]
    [Arguments("https://github.com/idefav/sing-box/tree/main")]
    [Arguments("owner/../other")]
    [Arguments("owner/..")]
    [Arguments("https://github.com@evil.example/a/b")]
    public async Task RejectsInvalidRepository(string value)
    {
        var rejected = false;
        try { UpdateSources.NormalizeRepository(value); } catch (ArgumentException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task DraftResetDoesNotChangeConfigUntilSaved_AndRestoresAllSevenDefaults()
    {
        var config = new Config { CheckUpdateItem = new() { UpdateViaProxy = false }, ConstItem = new() };
        config.CheckUpdateItem.RepositoryOverrides["sing_box"] = "idefav/sing-box";
        config.ConstItem.SrsSourceUrl = Global.SingboxRulesetSources[2];
        var rows = UpdateSourceItem.Create(config);
        foreach (var row in rows) row.Value = "";
        await UpdateSources.Repository(config, ECoreType.sing_box).Should().BeEqualTo("idefav/sing-box");
        UpdateSourceItem.Apply(rows, config);
        foreach (var type in UpdateSources.Programs)
            await UpdateSources.Repository(config, type).Should().BeEqualTo(Global.CoreUrls[type]);
        await config.CheckUpdateItem.UpdateViaProxy.Should().BeFalse();
        await config.ConstItem.SrsSourceUrl.Should().BeEqualTo("");
        await config.ConstItem.GeoSourceUrl.Should().BeEqualTo("");
        await config.ConstItem.RouteRulesTemplateSourceUrl.Should().BeEqualTo("");
    }

    [Test]
    public async Task AllExistingRulePresetsRemainValid()
    {
        foreach (var value in Global.GeoFilesSources) UpdateSources.ValidateTemplate(value, 1);
        foreach (var value in Global.SingboxRulesetSources) UpdateSources.ValidateTemplate(value, 2);
        UpdateSources.ValidateTemplate("https://example.com/{0}/{2}.srs", 2);
        foreach (var value in Global.RoutingRulesSources) UpdateSources.ValidateTemplate(value, 0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task InvalidDraftDoesNotPartiallyApply()
    {
        var config = new Config { CheckUpdateItem = new(), ConstItem = new() };
        var rows = UpdateSourceItem.Create(config);
        rows[0].Value = "custom/app";
        rows[5].Value = "https://example.com/{3}.srs";
        var rejected = false;
        try { UpdateSourceItem.Apply(rows, config); } catch (FormatException) { rejected = true; }
        await rejected.Should().BeTrue();
        await config.CheckUpdateItem.RepositoryOverrides.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task ConfigRoundtripKeepsOverridesAndOlderConfigUsesDefaults()
    {
        var config = JsonSerializer.Deserialize<Config>("""{"CheckUpdateItem":{"UpdateViaProxy":true}}""")!;
        await UpdateSources.Repository(config, ECoreType.v2rayN).Should().BeEqualTo("2dust/v2rayN");
        config.CheckUpdateItem.RepositoryOverrides["Xray"] = "custom/Xray-core";
        var restored = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
        await UpdateSources.Repository(restored, ECoreType.Xray).Should().BeEqualTo("custom/Xray-core");
    }

    [Test]
    public async Task ReleaseSelectionSkipsDraftsAndInvalidVersions_AndHonorsPrerelease()
    {
        GitHubRelease[] releases = [new() { TagName = "latest" }, new() { TagName = "v9.0.0", Draft = true },
            new() { TagName = "v2.0.0-beta.1", Prerelease = true }, new() { TagName = "v1.2.3" }];
        await UpdateSources.SelectRelease(releases, false, null).TagName.Should().BeEqualTo("v1.2.3");
        await UpdateSources.SelectRelease(releases, true, null).TagName.Should().BeEqualTo("v2.0.0-beta.1");
        await UpdateSources.SelectRelease(releases, true, new SemanticVersion("1.9.0")).TagName.Should().BeEqualTo("v1.2.3");
    }

    [Test]
    [Arguments(ECoreType.v2rayN, "windows", Architecture.X64, true, "v7.25.6-test.1", "v2rayN-windows-64-desktop.zip")]
    [Arguments(ECoreType.v2rayN, "windows", Architecture.Arm64, false, "v7.25.6", "v2rayN-windows-arm64.zip")]
    [Arguments(ECoreType.v2rayN, "macos", Architecture.Arm64, false, "v7.25.6", "v2rayN-macos-arm64.zip")]
    [Arguments(ECoreType.sing_box, "macos", Architecture.Arm64, false, "v1.14.3-ssh-udp.1", "sing-box-1.14.3-ssh-udp.1-darwin-arm64.tar.gz")]
    [Arguments(ECoreType.Xray, "linux", Architecture.RiscV64, false, "v26.1.1", "Xray-linux-riscv64.zip")]
    [Arguments(ECoreType.mihomo, "linux", Architecture.LoongArch64, false, "v1.19.0", "mihomo-linux-loong64-abi2-v1.19.0.gz")]
    [Arguments(ECoreType.mihomo, "windows", Architecture.X64, false, "v1.19.0", "mihomo-windows-amd64-v1-v1.19.0.zip")]
    public async Task PlatformAssetSelectionPreservesTag(ECoreType type, string platform, Architecture arch, bool desktop, string tag, string expected)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(type)!;
        await UpdateSources.AssetName(info, tag, platform, arch, desktop).Should().BeEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("latest")]
    [Arguments("v1.2.3.4")]
    [Arguments("v999999999999999.0.0")]
    [Arguments("v1.2.3-")]
    public async Task InvalidVersionsAreNotSilentlyTreatedAsZero(string? value)
    {
        await UpdateSources.TryVersion(value, out _).Should().BeFalse();
        var rejected = false;
        try { UpdateSources.SelectRelease([new() { TagName = value }], true, null); }
        catch (InvalidOperationException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task RepositoryIdentityIsIndependentOfVersion()
    {
        await UpdateSources.IsSourceSwitch("SagerNet/sing-box", "idefav/sing-box").Should().BeTrue();
        await UpdateSources.IsSourceSwitch("SAGERNET/sing-box", "SagerNet/sing-box").Should().BeFalse();
        await UpdateSources.IsSourceSwitch(null, "SagerNet/sing-box").Should().BeTrue();
    }
}
