using CommandLine;

namespace Downloader;

public class DownloadCommandParameters
{
    [Option('t', "log-tag")]
    public string? LogTag { get; set; }

    [Option('d', "directory", Required = true)]
    public string? Directory { get; set; }

    [Option('c', "concurrent-count", Required = true)]
    public int ConcurrentDownloadCount { get; set; }

    [Option('r', "retry-count", Required = true)]
    public int? RetryCount { get; set; }

    [Option('u', "username")]
    public string? Username { get; set; }

    [Option('p', "password")]
    public string? Password { get; set; }

    [Option('i', "create-content-info")]
    public bool CreateContentInfo { get; set; }
}

