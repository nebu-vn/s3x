using System.Text.Json.Serialization;
using S3Explorer.Services;

namespace S3Explorer.Models;

/// <summary>An S3 profile (bucket + prefix) added by the user.</summary>
public sealed class S3Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Endpoint for S3-compatible services (MinIO, Wasabi, R2, ...). Empty = AWS S3.</summary>
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "ap-southeast-1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "";
    public bool ForcePathStyle { get; set; }

    public string AccessKey { get; set; } = "";

    /// <summary>Secret key encrypted with Windows DPAPI (only the current Windows user can decrypt it).</summary>
    public string EncryptedSecretKey { get; set; } = "";

    [JsonIgnore]
    public string SecretKey
    {
        get => Crypto.Unprotect(EncryptedSecretKey);
        set => EncryptedSecretKey = Crypto.Protect(value);
    }

    /// <summary>Cache summary line shown in the list (not saved to JSON).</summary>
    [JsonIgnore]
    public string Summary { get; set; } = "";

    /// <summary>Time of the last scan (display only, not saved to JSON).</summary>
    [JsonIgnore]
    public string ScanTime { get; set; } = "";

    /// <summary>Normalized prefix: no leading '/', always ends with '/' (or empty).</summary>
    [JsonIgnore]
    public string NormalizedPrefix
    {
        get
        {
            var p = (Prefix ?? "").Trim().TrimStart('/');
            return p.Length == 0 || p.EndsWith('/') ? p : p + "/";
        }
    }

    [JsonIgnore]
    public string Display => $"{Bucket}/{NormalizedPrefix}" +
                             (string.IsNullOrWhiteSpace(ServiceUrl) ? "" : $"  @ {ServiceUrl.Trim()}");

    /// <summary>True if the change alters the file listing (cache must be cleared).</summary>
    public bool ScopeDiffers(S3Profile other) =>
        !string.Equals(ServiceUrl.Trim(), other.ServiceUrl.Trim(), StringComparison.OrdinalIgnoreCase) ||
        Bucket.Trim() != other.Bucket.Trim() ||
        NormalizedPrefix != other.NormalizedPrefix;

    public S3Profile Clone()
    {
        var c = new S3Profile { Id = Id };
        c.CopyFrom(this);
        return c;
    }

    public void CopyFrom(S3Profile o)
    {
        Name = o.Name;
        ServiceUrl = o.ServiceUrl;
        Region = o.Region;
        Bucket = o.Bucket;
        Prefix = o.Prefix;
        ForcePathStyle = o.ForcePathStyle;
        AccessKey = o.AccessKey;
        EncryptedSecretKey = o.EncryptedSecretKey;
    }

    public override string ToString() => Name;
}
