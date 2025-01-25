using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Downloader;

public class ContentInfo
{
    public string Name { get; set; }
    public string Hash { get; set; }
    public long Length { get; set; }


    public static async Task<ContentInfo> CreateAsync(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException(nameof(path));

        var fileInfo = new FileInfo(path);
        var contentInfo = new ContentInfo()
        {
            Name = fileInfo.Name,
            Length = fileInfo.Length,
            Hash = Convert.ToHexString(await GetSHA1HashAsync(fileInfo))
        };

        return contentInfo;
    }

    private static async Task<byte[]> GetSHA1HashAsync(FileInfo fileInfo)
    {
        using FileStream stream = File.OpenRead(fileInfo.FullName);
        return await SHA1.Create().ComputeHashAsync(stream);
    }

    public override string ToString()
       => $"{Name}";
}
