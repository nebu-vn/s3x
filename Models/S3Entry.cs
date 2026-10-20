using System.ComponentModel;
using System.Windows.Media;
using S3Explorer.Services;

namespace S3Explorer.Models;

/// <summary>Một dòng trong danh sách kiểu Explorer: thư mục hoặc file.</summary>
public sealed class S3Entry : INotifyPropertyChanged
{
    public bool IsFolder { get; init; }

    /// <summary>Tên hiển thị (không có đường dẫn).</summary>
    public string Name { get; init; } = "";

    /// <summary>Đường dẫn tương đối so với prefix của cấu hình. Thư mục kết thúc bằng '/'.</summary>
    public string RelativePath { get; init; } = "";

    public string Extension { get; init; } = "";
    public long Size { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public string StorageClass { get; init; } = "";

    /// <summary>Thư mục: tổng số file bên trong (gồm cả thư mục con).</summary>
    public long FileCount { get; init; }

    public string TypeName { get; set; } = "";
    public ImageSource? Icon { get; set; }

    private string? _localPath;

    /// <summary>File đã tải về máy: đường dẫn bản cục bộ (null = chưa tải hoặc bản tải đã cũ / bị xóa).</summary>
    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (_localPath == value) return;
            _localPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDownloaded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadTip)));
        }
    }

    public bool IsDownloaded => _localPath != null;
    public string? DownloadTip => _localPath == null ? null : $"Đã tải về: {_localPath}\nNhấp đúp để mở";

    public string SizeText => FormatUtil.Size(Size);
    public string CountText => IsFolder ? FileCount.ToString("N0") : "";
    public string ModifiedText => LastModifiedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
