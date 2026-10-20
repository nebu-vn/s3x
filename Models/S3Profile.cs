using System.Text.Json.Serialization;
using S3Explorer.Services;

namespace S3Explorer.Models;

/// <summary>Một cấu hình S3 (bucket + prefix) mà người dùng đã thêm.</summary>
public sealed class S3Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Endpoint cho S3-compatible (MinIO, Wasabi, R2, ...). Để trống = AWS S3.</summary>
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "ap-southeast-1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "";
    public bool ForcePathStyle { get; set; }

    public string AccessKey { get; set; } = "";

    /// <summary>Secret key mã hóa bằng Windows DPAPI (chỉ user Windows hiện tại giải mã được).</summary>
    public string EncryptedSecretKey { get; set; } = "";

    [JsonIgnore]
    public string SecretKey
    {
        get => Crypto.Unprotect(EncryptedSecretKey);
        set => EncryptedSecretKey = Crypto.Protect(value);
    }

    /// <summary>Dòng tóm tắt cache hiển thị trong danh sách (không lưu vào JSON).</summary>
    [JsonIgnore]
    public string Summary { get; set; } = "";

    /// <summary>Thời điểm quét gần nhất (hiển thị, không lưu vào JSON).</summary>
    [JsonIgnore]
    public string ScanTime { get; set; } = "";

    /// <summary>Prefix chuẩn hóa: không có '/' đầu, luôn kết thúc bằng '/' (hoặc rỗng).</summary>
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

    /// <summary>True nếu thay đổi làm danh sách file khác đi (cần xóa cache).</summary>
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
