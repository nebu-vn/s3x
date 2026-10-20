using System.Diagnostics;
using S3Explorer.Models;

namespace S3Explorer.Services;

public sealed record ScanProgress(long Files, long Bytes);

/// <summary>Scan metadata from S3 and overwrite a profile's cache.</summary>
public static class Scanner
{
    public static async Task<ScanInfo> ScanAsync(S3Profile profile, CacheStore cache,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var prefix = profile.NormalizedPrefix;

        using var client = S3Service.CreateClient(profile);
        using var session = cache.BeginReplace(profile.Id);

        await foreach (var page in S3Service.ListAllAsync(client, profile.Bucket.Trim(), prefix, ct).ConfigureAwait(false))
        {
            foreach (var o in page)
            {
                if (!o.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                session.Add(o.Key[prefix.Length..], o.Size, o.LastModifiedTicksUtc, o.StorageClass);
            }
            progress?.Report(new ScanProgress(session.FileCount, session.TotalBytes));
        }

        ct.ThrowIfCancellationRequested();
        return session.Commit(sw.ElapsedMilliseconds);
    }
}
