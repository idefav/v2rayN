namespace ServiceLib.Tests.Services;

public class MacProxyRecoveryTests
{
    [Test]
    public async Task ReadsActualProxyFlagsAndEndpoint()
    {
        var actual = "<dictionary> {\n HTTPEnable : 1\n HTTPProxy : 127.0.0.1\n HTTPPort : 10808\n HTTPSEnable : 1\n HTTPSProxy : 127.0.0.1\n HTTPSPort : 10808\n SOCKSEnable : 1\n SOCKSProxy : 127.0.0.1\n SOCKSPort : 10808\n}";
        await MacRecoveryPlatform.ParseProxy(actual, 10808).Matches.Should().BeTrue();
        await MacRecoveryPlatform.ParseProxy(actual, 10809).Matches.Should().BeFalse();
        await MacRecoveryPlatform.ParseProxy(actual.Replace("HTTPEnable : 1", "HTTPEnable : 0"), 10808).Matches.Should().BeFalse();
        await MacRecoveryPlatform.ParseProxy("<dictionary> {\n}", 10808).Matches.Should().BeFalse();
        await MacRecoveryPlatform.ParseProxy("<dictionary> {\n}", 0).Matches.Should().BeTrue();
        await MacRecoveryPlatform.ParseProxy("", 0).Matches.Should().BeFalse();
    }

    [Test]
    public async Task ScriptPreservesNamesSkipsDisabledServicesAndPropagatesPartialFailure()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix shell fixture"); return; }
        var directory = Path.Combine(Path.GetTempPath(), "proxy-script-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "proxy.sh");
            await File.WriteAllTextAsync(script, EmbedUtils.GetEmbedText(Global.ProxySetOSXShellFileName));
            var fake = Path.Combine(directory, "networksetup");
            await File.WriteAllTextAsync(fake, """
                #!/bin/bash
                if [[ "$1" == "-listallnetworkservices" ]]; then
                  printf 'An asterisk denotes disabled services.\nWi-Fi\nUSB Ethernet Adapter\n*Disabled Service\n'
                  exit 0
                fi
                printf '%s|%s\n' "$1" "$2" >> "$PROXY_TEST_LOG"
                if [[ "${PROXY_TEST_FAIL:-0}" == 1 && "$1" == "-setsecurewebproxy" ]]; then exit 7; fi
                """ + "\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var fail in new[] { false, true })
            {
                var capture = Path.Combine(directory, fail ? "failed.log" : "success.log");
                var info = new ProcessStartInfo("/bin/bash") { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { script, "set", "127.0.0.1", "10808", "localhost" }) info.ArgumentList.Add(arg);
                info.Environment["PATH"] = directory + ":" + Environment.GetEnvironmentVariable("PATH");
                info.Environment["PROXY_TEST_LOG"] = capture;
                info.Environment["PROXY_TEST_FAIL"] = fail ? "1" : "0";
                using var process = Process.Start(info)!;
                await process.WaitForExitAsync();
                var lines = await File.ReadAllLinesAsync(capture);
                if ((process.ExitCode != 0) != fail || lines.Length != 8 || lines.Any(l => l.Contains("Disabled"))
                    || !lines.Contains("-setwebproxy|USB Ethernet Adapter")) throw new Exception("proxy script lost names, service filtering or failure status");
            }
            var failureNotified = false;
            if (await SystemProxyCommand.Run("/bin/bash", ["-c", "exit 9"], _ => failureNotified = true) || !failureNotified)
                throw new Exception("command failure reported success");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task CachedBundledScriptIsUpdated()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix shell permissions"); return; }
        var name = "proxy-cache-test-" + Guid.NewGuid() + ".sh";
        var path = await FileUtils.CreateLinuxShellFile(name, "#!/bin/bash\nexit 1\n", true);
        try
        {
            await FileUtils.CreateLinuxShellFile(name, "#!/bin/bash\nexit 0\n", true);
            if (!(await File.ReadAllTextAsync(path)).Contains("exit 0")) throw new Exception("old cached helper was retained");
        }
        finally { File.Delete(path); }
    }
}
