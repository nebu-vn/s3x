using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using S3Explorer.Models;

namespace S3Explorer.Services;

public sealed record S3ObjectInfo(string Key, long Size, long LastModifiedTicksUtc, string? StorageClass);

/// <summary>S3 access: list metadata, download files, copy / delete objects.</summary>
public static class S3Service
{
    public static IAmazonS3 CreateClient(S3Profile p)
    {
        var cfg = new AmazonS3Config { ForcePathStyle = p.ForcePathStyle };
        var region = (p.Region ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(p.ServiceUrl))
        {
            cfg.ServiceURL = p.ServiceUrl.Trim();
            if (region.Length > 0) cfg.AuthenticationRegion = region;
            // many S3-compatible services reject the SDK's default trailing checksums on uploads
            cfg.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            cfg.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        else
        {
            cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(region.Length > 0 ? region : "us-east-1");
        }

        AWSCredentials creds = string.IsNullOrWhiteSpace(p.AccessKey)
            ? new AnonymousAWSCredentials()
            : new BasicAWSCredentials(p.AccessKey.Trim(), p.SecretKey);

        return new AmazonS3Client(creds, cfg);
    }

    /// <summary>Test connection: list 1 object.</summary>
    public static async Task TestAsync(S3Profile p, CancellationToken ct = default)
    {
        using var client = CreateClient(p);
        await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = p.Bucket.Trim(),
            Prefix = p.NormalizedPrefix,
            MaxKeys = 1
        }, ct).ConfigureAwait(false);
    }

    /// <summary>List all objects under a prefix (recursively), yielding one page of ≤1000 objects at a time.</summary>
    public static async IAsyncEnumerable<IReadOnlyList<S3ObjectInfo>> ListAllAsync(
        IAmazonS3 client, string bucket, string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var req = new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1000 };
        if (prefix.Length > 0) req.Prefix = prefix;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await client.ListObjectsV2Async(req, ct).ConfigureAwait(false);

            var page = new List<S3ObjectInfo>(resp.S3Objects?.Count ?? 0);
            if (resp.S3Objects != null)
            {
                foreach (var o in resp.S3Objects)
                {
                    var modified = (DateTime?)o.LastModified;
                    page.Add(new S3ObjectInfo(
                        o.Key,
                        (long?)o.Size ?? 0,
                        modified?.ToUniversalTime().Ticks ?? 0,
                        o.StorageClass?.Value));
                }
            }
            yield return page;

            if (resp.IsTruncated != true || string.IsNullOrEmpty(resp.NextContinuationToken)) break;
            req.ContinuationToken = resp.NextContinuationToken;
        }
    }

    /// <summary>Download an object to a local file (writes to .part, then renames when done).</summary>
    public static async Task DownloadAsync(IAmazonS3 client, string bucket, string key, string localPath,
        Action<long>? onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var tmp = localPath + ".part";
        try
        {
            using (var resp = await client.GetObjectAsync(bucket, key, ct).ConfigureAwait(false))
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await resp.ResponseStream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    onBytes?.Invoke(n);
                }
            }
            File.Move(tmp, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>True if both profiles reach the same endpoint with the same credentials, so objects can be copied server-side.</summary>
    public static bool SameAccount(S3Profile a, S3Profile b) =>
        string.Equals(a.ServiceUrl.Trim(), b.ServiceUrl.Trim(), StringComparison.OrdinalIgnoreCase) &&
        a.AccessKey.Trim().Length > 0 &&
        a.AccessKey.Trim() == b.AccessKey.Trim() &&
        a.SecretKey == b.SecretKey;

    private const long MaxSingleCopy = 5L * 1024 * 1024 * 1024;   // CopyObject limit
    private const long MinCopyPart = 512L * 1024 * 1024;

    /// <summary>
    /// Server-side copy (no data passes through this machine). Objects over 5 GB are copied with a multipart copy.
    /// The client must be the destination profile's client and be allowed to read the source.
    /// </summary>
    public static async Task CopyServerSideAsync(IAmazonS3 client, string srcBucket, string srcKey, long size,
        string destBucket, string destKey, CancellationToken ct)
    {
        if (size <= MaxSingleCopy)
        {
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = srcBucket,
                SourceKey = srcKey,
                DestinationBucket = destBucket,
                DestinationKey = destKey
            }, ct).ConfigureAwait(false);
            return;
        }

        // A multipart copy does not carry metadata over by itself
        var meta = await client.GetObjectMetadataAsync(srcBucket, srcKey, ct).ConfigureAwait(false);
        var initReq = new InitiateMultipartUploadRequest { BucketName = destBucket, Key = destKey, ContentType = meta.Headers.ContentType };
        foreach (var k in meta.Metadata.Keys) initReq.Metadata[k] = meta.Metadata[k];
        var init = await client.InitiateMultipartUploadAsync(initReq, ct).ConfigureAwait(false);

        try
        {
            long partSize = Math.Max(MinCopyPart, (size + 9999) / 10000);
            var parts = new List<CopyPartResponse>();
            int partNo = 1;
            for (long pos = 0; pos < size; pos += partSize, partNo++)
            {
                parts.Add(await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = srcBucket,
                    SourceKey = srcKey,
                    DestinationBucket = destBucket,
                    DestinationKey = destKey,
                    UploadId = init.UploadId,
                    PartNumber = partNo,
                    FirstByte = pos,
                    LastByte = Math.Min(pos + partSize, size) - 1
                }, ct).ConfigureAwait(false));
            }

            var done = new CompleteMultipartUploadRequest { BucketName = destBucket, Key = destKey, UploadId = init.UploadId };
            done.AddPartETags(parts);
            await client.CompleteMultipartUploadAsync(done, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    { BucketName = destBucket, Key = destKey, UploadId = init.UploadId }).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Copy between different accounts / endpoints: download to a temp file, then upload it
    /// (multipart for large files). Content-Type and user metadata are preserved.
    /// </summary>
    public static async Task CopyViaLocalAsync(IAmazonS3 srcClient, string srcBucket, string srcKey,
        IAmazonS3 destClient, string destBucket, string destKey, string tempDir, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var tmp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var up = new TransferUtilityUploadRequest { FilePath = tmp, BucketName = destBucket, Key = destKey };
            using (var resp = await srcClient.GetObjectAsync(srcBucket, srcKey, ct).ConfigureAwait(false))
            {
                up.ContentType = resp.Headers.ContentType;
                foreach (var k in resp.Metadata.Keys) up.Metadata[k] = resp.Metadata[k];
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await resp.ResponseStream.CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
            }

            using var tu = new TransferUtility(destClient);
            await tu.UploadAsync(up, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Delete a list of keys (DeleteObjects, 1000 keys per call; falls back to one key at a time if the endpoint doesn't support it).
    /// Returns the keys that failed to delete.
    /// </summary>
    public static async Task<List<string>> DeleteKeysAsync(IAmazonS3 client, string bucket, IReadOnlyList<string> keys,
        Action<int>? onDone, CancellationToken ct)
    {
        var failed = new List<string>();
        int done = 0;
        for (int i = 0; i < keys.Count; i += 1000)
        {
            ct.ThrowIfCancellationRequested();
            var batch = keys.Skip(i).Take(1000).ToList();
            try
            {
                var resp = await client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Quiet = true,
                    Objects = batch.Select(k => new KeyVersion { Key = k }).ToList()
                }, ct).ConfigureAwait(false);
                if (resp.DeleteErrors != null) failed.AddRange(resp.DeleteErrors.Select(e => e.Key));
            }
            catch (DeleteObjectsException ex)
            {
                if (ex.Response?.DeleteErrors != null) failed.AddRange(ex.Response.DeleteErrors.Select(e => e.Key));
                else failed.AddRange(batch);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotImplemented
                                                   or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest)
            {
                foreach (var k in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await client.DeleteObjectAsync(bucket, k, ct).ConfigureAwait(false); }
                    catch (AmazonS3Exception) { failed.Add(k); }
                }
            }
            done += batch.Count;
            onDone?.Invoke(done);
        }
        return failed;
    }
}
