using System.ComponentModel;
using System.Windows.Media;
using S3Explorer.Services;

namespace S3Explorer.Models;

/// <summary>A row in the Explorer-style listing: a folder or a file.</summary>
public sealed class S3Entry : INotifyPropertyChanged
{
    public bool IsFolder { get; init; }

    /// <summary>Display name (without path).</summary>
    public string Name { get; init; } = "";

    /// <summary>Path relative to the profile's prefix. Folders end with '/'.</summary>
    public string RelativePath { get; init; } = "";

    public string Extension { get; init; } = "";
    public long Size { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public string StorageClass { get; init; } = "";

    /// <summary>Folder: total number of files inside (including subfolders).</summary>
    public long FileCount { get; init; }

    public string TypeName { get; set; } = "";
    public ImageSource? Icon { get; set; }

    private string? _localPath;

    /// <summary>Downloaded file: path of the local copy (null = not downloaded, or the copy is stale / deleted).</summary>
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
    public string? DownloadTip => _localPath == null ? null : $"Downloaded to: {_localPath}\nDouble-click to open";

    public string SizeText => FormatUtil.Size(Size);
    public string CountText => IsFolder ? FileCount.ToString("N0") : "";
    public string ModifiedText => LastModifiedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
