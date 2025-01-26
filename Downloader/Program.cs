using CommandLine;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Downloader;

//Downloader -d "directory" -c 5 -r 10 -u "user" -p "pass"
internal partial class Program
{
    public const int DirectoryNotFoundCode = -1;
    public const int AllNotDownloadedCode = -2;
    public const int AllDownloadedCode = 0;
    private const string ContentInfoDirectoryName = "ContentInfo";

    private static HttpClientHandler BypassSSLHandler => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors) => true
    };


    static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        //Test(); return;

        Parser.Default.ParseArguments<DownloadCommandParameters>(args)
            .WithParsed(parameters => Handle(parameters));
    }

    private static void Test()
    {
        Handle(new DownloadCommandParameters()
        {
            Directory = "E:\\TestDownloader\\Items",
            ConcurrentDownloadCount = 5,
            RetryCount = 15,
            Username = null,
            Password = null,
            CreateContentInfo = true,
        });
    }

    private static void Handle(DownloadCommandParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.LogTag))
            parameters.LogTag = Guid.NewGuid().ToString("N").ToUpper();

        if (!Directory.Exists(parameters.Directory))
        {
            Log.Error("Downloader {LogTag}> Directory '{Directory}' not found!", parameters.LogTag, parameters.Directory);
            Environment.Exit(DirectoryNotFoundCode);
        }

        var downloadItems = new List<DownloadItem>();
        foreach (var filename in Directory.GetFiles(parameters.Directory, "*.download"))
        {
            try
            {
                var content = File.ReadAllText(filename);
                var downloadItem = JsonSerializer.Deserialize<DownloadItem>(content);
                if (downloadItem is null)
                {
                    Log.Error("Downloader {LogTag}> File '{Filename}' is wrong!", parameters.LogTag, filename);
                    continue;
                }
                downloadItem.Filename = filename;
                downloadItems.Add(downloadItem);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Downloader {LogTag}> File '{Filename}' is wrong!", parameters.LogTag, filename);
            }
        }

        Log.Information("Downloader {LogTag}> Download {Count} \r\n" +
                        " - Directory: '{Directory}' \r\n" +
                        " - ConcurrentDownload: '{ConcurrentDownload}' \r\n" +
                        " - RetryCount: '{RetryCount}' \r\n" +
                        " - Username: '{Username}' \r\n" +
                        " - Password: {Password}",
            parameters.LogTag, downloadItems.Count, parameters.Directory, parameters.ConcurrentDownloadCount,
            parameters.RetryCount, parameters.Username, (parameters.Username is null ? null : "*****"));

        var allDownloaded = Download(parameters, downloadItems).GetAwaiter().GetResult();

        if (!allDownloaded)
        {
            Log.Error("Downloader {LogTag}> All not downloaded! ({Directory})", parameters.LogTag, parameters.Directory);
            Environment.Exit(AllNotDownloadedCode);
        }

        Log.Information("Downloader {LogTag}> All downloaded. ({Directory})", parameters.LogTag, parameters.Directory);
        Environment.Exit(AllDownloadedCode);
    }

    private static async Task<bool> Download(DownloadCommandParameters parameters, List<DownloadItem> downloadItems)
    {
        var downloadTasks = new List<Task>();
        var throttler = new SemaphoreSlim(parameters.ConcurrentDownloadCount);
        var downloadedCount = 0l;

        foreach (var downloadItem in downloadItems)
        {
            var item = downloadItem;
            await throttler.WaitAsync();
            downloadTasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await Download(parameters, downloadItem))
                        Interlocked.Increment(ref downloadedCount);
                }
                finally
                {
                    throttler.Release();
                }
            }));
        }
        await Task.WhenAll(downloadTasks);

        return downloadedCount == downloadItems.Count;
    }

    private static async Task<bool> Download(DownloadCommandParameters parameters, DownloadItem downloadItem)
    {
        var httpClient = new HttpClient(BypassSSLHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var outputDirectory = parameters.Directory;
        var outputFileName = Path.Combine(parameters.Directory, downloadItem.Name.FixInvalidChars("-"));

        for (int retryNumber = 1; retryNumber <= parameters.RetryCount; retryNumber++)
        {
            var stopwatch = new Stopwatch();
            try
            {
                Log.Information("Downloader {LogTag}> Download started [timeout:{TimeoutSeconds}s]> {Title} \r\n  - Address: {Address}",
                    parameters.LogTag, downloadItem.Timeout.TotalSeconds, downloadItem.Name, downloadItem.Address);

                stopwatch.Restart();
                using var requestMessage = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(downloadItem.Address)
                };

                if (parameters.Username != null && parameters.Password != null)
                {
                    var authenticationString = $"{parameters.Username}:{parameters.Password}";
                    var base64EncodedAuthenticationString = Convert.ToBase64String(Encoding.ASCII.GetBytes(authenticationString));
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64EncodedAuthenticationString);
                }

                using (var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, new CancellationTokenSource(downloadItem.Timeout).Token))
                using (var remoteStream = await response.Content.ReadAsStreamAsync())
                using (var content = File.Create(outputFileName))
                {
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await content.WriteAsync(buffer, 0, read);
                    }
                    await content.FlushAsync();
                }

                if (parameters.CreateContentInfo)
                    await CreateContentInfo(parameters.LogTag, outputDirectory, outputFileName);

                File.Delete(downloadItem.Filename);
                stopwatch.Stop();
                Log.Information("Downloader {LogTag}> Download completed [retry #{RetryNumber} - timeout:{TimeoutSeconds}s]> {Title} @{ElapsedTotalSeconds:N2}s\r\n  - Address: {Address}",
                    parameters.LogTag, retryNumber, downloadItem.Timeout.TotalSeconds,
                    downloadItem.Name, stopwatch.Elapsed.TotalSeconds, downloadItem.Address);

                return true;
            }
            catch (Exception ex)
            {
                var waitTime = retryNumber * 250;
                if (retryNumber == parameters.RetryCount)
                    waitTime = 0;

                Log.Warning(ex, "Downloader {LogTag}> Download error [retry #{RetryNumber} - timeout:{TimeoutSeconds}s]> {Title} @{ElapsedTotalSeconds:N2}s (Wait: {WaitTime}ms)\r\n  - Address: {Address}",
                   parameters.LogTag, retryNumber, downloadItem.Timeout.TotalSeconds,
                   downloadItem.Name, stopwatch.Elapsed.TotalSeconds, waitTime, downloadItem.Address);
                await Task.Delay(waitTime);
            }
        }

        return false;
    }

    private static async Task CreateContentInfo(string logTag, string outputDirectory, string outputFileName)
    {
        var contentInfoDirectory = Path.Combine(outputDirectory, ContentInfoDirectoryName);
        if (!Directory.Exists(contentInfoDirectory))
            Directory.CreateDirectory(contentInfoDirectory);

        var contentInfo = await ContentInfo.CreateAsync(outputFileName);
        var contentInfoPath = Path.Combine(contentInfoDirectory, $"{contentInfo.Name}.json");
        await File.WriteAllTextAsync(contentInfoPath, JsonSerializer.Serialize(contentInfo));

        Log.Information("Downloader {LogTag}> contentInfo> Created on '{Path}'.", logTag, contentInfoPath);
    }
}

