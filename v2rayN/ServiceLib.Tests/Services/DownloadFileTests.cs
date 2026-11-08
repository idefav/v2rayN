using System.IO.Compression;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class DownloadFileTests
{
    [Test]
    [Arguments("recover")]
    [Arguments("504")]
    [Arguments("404")]
    [Arguments("429")]
    [Arguments("cancel")]
    [Arguments("truncated")]
    public async Task ActualDownloaderPreservesErrorsRetriesAndCleansPartialFiles(string mode)
    {
        await CertPemManager.Instance.Init(new Config { GuiItem = new GUIItem() });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gets = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var line = await reader.ReadLineAsync(stop.Token);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    var head = line!.StartsWith("HEAD ");
                    if (!head) gets++;
                    var status = head ? "200 OK" : mode == "404" ? "404 Not Found" : mode == "429" ? "429 Too Many Requests"
                        : mode == "recover" && gets > 1 || mode == "truncated" ? "200 OK" : "504 Gateway Time-out";
                    var body = head ? "" : mode == "truncated" ? "x" : "test-package";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 12\r\nConnection: close\r\n\r\n{body}"), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        var directory = Directory.CreateTempSubdirectory("download-test-").FullName;
        var path = Path.Combine(directory, "package.zip");
        var service = new DownloadService();
        var successes = 0;
        Exception? error = null;
        using var cancel = new CancellationTokenSource();
        service.UpdateCompleted += (_, state) =>
        {
            if (state.Success) successes++;
            if (mode == "cancel" && state.Msg!.Contains("Retrying")) cancel.Cancel();
        };
        service.Error += (_, state) => error = state.GetException();
        try
        {
            var cancelled = false;
            try
            {
                await service.DownloadFileAsync(new() { FileUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/package.zip", FilePath = path }, false, cancel.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            if (mode == "recover")
            {
                Check(gets == 2 && successes == 1 && error == null && File.ReadAllText(path) == "test-package", "retry did not complete correctly");
            }
            else
            {
                Check(successes == 0 && !File.Exists(path), "failed download reported success or left a partial package");
                if (mode == "cancel") Check(cancelled && gets == 1 && error == null, "cancellation retried or became a download error");
                else
                {
                    Check(gets == (mode == "404" ? 1 : 3), "unexpected retry count: " + gets);
                    Check(error != null && error.Message.Contains("direct") && error.Message.Contains("package.zip"), "missing download context");
                    if (mode != "truncated") Check(error!.Message.Contains(mode), "HTTP status was lost");
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await server;
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task RealPublishedZipDownloadsThroughProductionServiceAndMatchesDigest()
    {
        if (Environment.GetEnvironmentVariable("V2RAYN_TEST_REAL_DOWNLOAD") != "1")
        { Skip.Test("Opt-in real release download"); return; }
        await CertPemManager.Instance.Init(new Config { GuiItem = new GUIItem() });
        var directory = Directory.CreateTempSubdirectory("real-download-").FullName;
        var path = Path.Combine(directory, "package.zip");
        try
        {
            var service = new DownloadService();
            Exception? error = null;
            var completed = false;
            service.Error += (_, e) => error = e.GetException();
            service.UpdateCompleted += (_, s) => completed |= s.Success;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await service.DownloadFileAsync(new()
            {
                FileUrl = "https://github.com/idefav/v2rayN/releases/download/v7.25.6-ssh-preview.29/v2rayN-macos-arm64.zip",
                FilePath = path
            }, false, timeout.Token);
            if (error != null) throw error;
            Check(completed, "no successful download event");
            using var input = File.OpenRead(path);
            Check(Convert.ToHexString(SHA256.HashData(input)).Equals("dbf16684eb313791ecbdce20531983860aee271b0232489937853c0de57c102b", StringComparison.OrdinalIgnoreCase), "real release digest mismatch");
            using var zip = ZipFile.OpenRead(path);
            Check(zip.GetEntry("v2rayN-macos-arm64/AmazToolV2") != null && zip.GetEntry("v2rayN-macos-arm64/v2rayN") != null, "not the expected application package");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
