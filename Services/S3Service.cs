using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Models;

namespace S3Explorer.Services;

public sealed record S3ObjectInfo(string Key, long Size, long LastModifiedTicksUtc, string? StorageClass);

/// <summary>Truy cập S3: liệt kê metadata, tải file về, xóa object.</summary>
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

    /// <summary>Kiểm tra kết nối: list 1 object.</summary>
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

    /// <summary>Liệt kê toàn bộ object dưới prefix (đệ quy), mỗi lần trả một trang ≤1000 object.</summary>
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

    /// <summary>Tải một object về file cục bộ (ghi ra .part rồi đổi tên khi xong).</summary>
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
            try { File.Delete(tmp); } catch { /* bỏ qua */ }
            throw;
        }
    }

    /// <summary>
    /// Xóa danh sách key (DeleteObjects, 1000 key/lần; nếu endpoint không hỗ trợ thì xóa từng key).
    /// Trả về các key xóa thất bại.
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
