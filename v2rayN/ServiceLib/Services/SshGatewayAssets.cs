using System.IO.Compression;

namespace ServiceLib.Services;

internal static class SshGatewayAssets
{
    internal static string Resource(string name)
    {
        using var stream = typeof(SshGatewayAssets).Assembly.GetManifestResourceStream("ServiceLib.Sample." + name)
            ?? throw new SshSetupException("E_DEPLOY_RESOURCE");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static async Task<(string Helper, string HelperHash, string Binary, string BinaryHash)> PrepareAsync(string architecture, string directory, CancellationToken token)
    {
        if (architecture is not ("amd64" or "arm64")) throw new SshSetupException("E_ARCH_UNSUPPORTED");
        var root = JsonNode.Parse(Resource("udpgw-assets.json"))!;
        var asset = root["assets"]![architecture]!;
        var name = asset["name"]!.GetValue<string>();
        var expected = asset["sha256"]!.GetValue<string>();
        var archive = Path.Combine(directory, "gateway.zip");
        var download = new DownloadService();
        var failed = false;
        download.Error += (_, _) => failed = true;
        await download.DownloadFileAsync(new() { FileUrl = $"https://github.com/tun2proxy/tun2proxy/releases/download/v{root["version"]!.GetValue<string>()}/{name}", FilePath = archive }, true, token);
        if (failed || !File.Exists(archive)) throw new SshSetupException("E_DOWNLOAD");
        if (await HashAsync(archive, token) != expected) throw new SshSetupException("E_ARCHIVE_CHECKSUM");
        var binary = Path.Combine(directory, "udpgw-server");
        using (var zip = ZipFile.OpenRead(archive))
        {
            var entries = zip.Entries.Where(e => e.Name == "udpgw-server").ToArray();
            if (entries.Length != 1 || entries[0].Length is < 1 or > 67108864) throw new SshSetupException("E_ARCHIVE_CONTENT");
            using var source = entries[0].Open();
            using var dest = SshKeyStore.CreatePrivateFile(binary);
            await source.CopyToAsync(dest, token);
        }
        var relative = "udpgw-deploy-linux-" + architecture;
        var helper = Utils.GetBaseDirectory(Path.Combine("udpgw-deploy", relative));
        var manifest = Utils.GetBaseDirectory(Path.Combine("udpgw-deploy", "SHA256SUMS"));
        if (!File.Exists(helper) || !File.Exists(manifest)) throw new SshSetupException("E_DEPLOY_COMPONENT");
        var sums = await File.ReadAllLinesAsync(manifest, token);
        var entry = sums.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).SingleOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == relative);
        var helperHash = await HashAsync(helper, token);
        if (entry == null || entry[0] != helperHash) throw new SshSetupException("E_HELPER_CHECKSUM");
        return (helper, helperHash, binary, await HashAsync(binary, token));
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
}
