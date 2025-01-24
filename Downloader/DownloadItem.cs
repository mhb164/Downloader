namespace Downloader;

public class DownloadItem
{
    public string Filename { get; set; }

    public string Name { get; set; }
    public string Address { get; set; }
    public TimeSpan Timeout { get; set; }
}

