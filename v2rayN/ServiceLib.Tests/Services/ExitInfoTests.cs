using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

public class ExitInfoTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    [Test]
    public void IndependentCoreRoutesToTargetWithoutSystemProxyOrTun()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        var node = ServiceLib.Tests.CoreConfig.Singbox.SshTests.Node();
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        var generated = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(29999);
        Check(generated.Success, "test configuration generation failed");
        var json = JsonNode.Parse(generated.Data!.ToString()!)!;
        Check(json["route"]!["final"]!.GetValue<string>() == Global.ProxyTag, "exit query could bypass target outbound");
        Check(json["route"]!["rules"]!.AsArray().Count == 0, "normal routing leaked into independent test");
        Check(json["inbounds"]!.AsArray().Count == 1 && json["inbounds"]![0]!["listen"]!.GetValue<string>() == Global.Loopback,
            "independent test must expose only a loopback proxy");
        Check(json["inbounds"]![0]!["type"]!.GetValue<string>() == "mixed", "independent test must not create a TUN");
    }

    [Test]
    public void ParsesProviderShapesAndAddressFamilies()
    {
        var v4 = ExitInfoService.Parse("""{"ip":"203.0.113.7","country_code":"jp","country":"Japan","region":"Tokyo","city":"Tokyo"}""");
        Check(v4.Ip == "203.0.113.7" && v4.CountryCode == "JP" && v4.City == "Tokyo", "IP.SB response");
        var v6 = ExitInfoService.Parse("""{"ip":"2001:db8::7","location":{"country_code":"SG","country":"Singapore","city":"Singapore"}}""");
        Check(v6.Ip == "2001:db8::7" && v6.CountryCode == "SG", "nested IPv6 response");
        var partial = ExitInfoService.Parse("""{"query":"203.0.113.8","country":"Unknown location"}""");
        Check(partial.CountryCode == null && partial.City == null, "do not create flag from country text");
    }

    [Test]
    public void InvalidResponsesNeverBecomeSuccessfulExits()
    {
        foreach (var input in new[] { "{}", "[]", "not json", "{\"ip\":\"localhost\"}", "{\"ip\":\"127.0.0.1\"}", "{\"ip\":\"10.0.0.1\"}", "{\"ip\":\"fd00::1\"}" })
        {
            var rejected = false;
            try { ExitInfoService.Parse(input); } catch (Exception ex) when (ex is FormatException or JsonException) { rejected = true; }
            Check(rejected, "invalid response was accepted: " + input);
        }
    }

    [Test]
    public void MetadataChangesDoNotInvalidateButConnectionChangesDo()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        var node = new ProfileItem { IndexId = "a", Remarks = "first", Address = "example.com", Port = 22, ConfigType = EConfigType.SSH, Username = "fixture", Password = "fixture" };
        var original = ExitInfoService.Fingerprint(node, config);
        node.Remarks = "renamed"; node.Subid = "new-folder";
        Check(original == ExitInfoService.Fingerprint(node, config), "metadata changed fingerprint");
        node.Port = 2222;
        Check(original != ExitInfoService.Fingerprint(node, config), "port did not change fingerprint");
        node.Port = 22; node.Password = "different";
        Check(original != ExitInfoService.Fingerprint(node, config), "credentials did not change fingerprint");
    }

    [Test]
    public void CachePolicyHonorsTtlCooldownAndConfiguration()
    {
        var now = DateTime.UtcNow;
        var entry = new ExitInfoItem { Fingerprint = "a", State = "success", CheckedAt = now.AddHours(-25) };
        Check(!ExitInfoService.NeedsCheck(entry, "a", false, now), "inactive cached nodes must not rescan periodically");
        Check(ExitInfoService.NeedsCheck(entry, "a", true, now), "active expired cache must refresh");
        entry.State = "failed"; entry.RetryAfter = now.AddMinutes(30);
        Check(!ExitInfoService.NeedsCheck(entry, "a", true, now), "failure cooldown ignored");
        Check(ExitInfoService.NeedsCheck(entry, "b", true, now), "changed configuration must invalidate cache");
        entry.State = "checking"; entry.RetryAfter = default;
        Check(!ExitInfoService.NeedsCheck(entry, "a", true, now), "running task must not duplicate");
    }

    [Test]
    public async Task QueryUsesSpecifiedProxyAndHonorsCancellationAndRateLimit()
    {
        // The destination is deliberately unresolvable. Only the disposable SOCKS fixture can answer it.
        foreach (var exit in new[] { "203.0.113.11", "2001:db8::12" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serving = Serve(listener, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{{\"ip\":\"{exit}\",\"country_code\":\"JP\"}}", timeout.Token);
            var response = await ExitInfoService.QueryAsync("http://exit-fixture.invalid/geoip", new WebProxy($"socks5://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), timeout.Token);
            Check(response.Ip == exit, "result did not come from the selected proxy");
            await serving;
        }
        using var limited = new TcpListener(IPAddress.Loopback, 0);
        limited.Start();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rateServer = Serve(limited, "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 3600\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct.Token);
        var proxy = new WebProxy($"socks5://127.0.0.1:{((IPEndPoint)limited.LocalEndpoint).Port}");
        var rateLimited = false;
        try { await ExitInfoService.QueryAsync("http://exit-fixture.invalid/geoip", proxy, ct.Token); }
        catch (ExitInfoService.ExitRateLimitException ex) { rateLimited = ex.RetryAfter > DateTime.UtcNow.AddMinutes(59); }
        Check(rateLimited, "Retry-After was ignored");
        await rateServer;
        ct.Cancel();
        var cancelled = false;
        try { await ExitInfoService.QueryAsync("http://exit-fixture.invalid/geoip", proxy, ct.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancelled lookup succeeded");
    }

    private static async Task Serve(TcpListener listener, string response, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct);
        var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, ct);
        await stream.WriteAsync(new byte[] { 5, 0 }, ct);
        var command = new byte[4]; await stream.ReadExactlyAsync(command, ct);
        Check(command[1] == 1 && command[3] == 3, "expected SOCKS connect with remote hostname");
        var length = new byte[1]; await stream.ReadExactlyAsync(length, ct);
        var host = new byte[length[0]]; await stream.ReadExactlyAsync(host, ct);
        Check(Encoding.ASCII.GetString(host) == "exit-fixture.invalid", "unexpected SOCKS target");
        var port = new byte[2]; await stream.ReadExactlyAsync(port, ct);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
        var request = new StringBuilder(); var single = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) { await stream.ReadExactlyAsync(single, ct); request.Append((char)single[0]); }
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
    }
}
