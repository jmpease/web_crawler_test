using System.Security.Cryptography;
using System.Text;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Fixed-size key for a normalized URL, so unique indexes don't hit btree row-size limits on very long URLs.
/// </summary>
public static class UrlHash
{
    public static byte[] Compute(Uri normalizedUrl) => Compute(normalizedUrl.AbsoluteUri);

    public static byte[] Compute(string normalizedUrl) => SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
}
