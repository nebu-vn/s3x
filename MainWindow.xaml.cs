using System.Collections;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using S3Explorer.Models;
using S3Explorer.Services;

namespace S3Explorer;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore = new();
    private readonly CacheStore _cache = new(Path.Combine(ProfileStore.AppDataDir, "cache.db"));
    private readonly ObservableCollection<S3Profile> _profiles = new();

    private S3Profile? _current;
    private string _currentPath = "";
    private readonly Stack<string> _back = new();

    private ListCollectionView? _view;
    private string _sortKey = "Name";
    private bool _sortAsc = true;

    private CancellationTokenSource? _opCts;          // download / delete
    private string? _noticePath;                        // file/folder for "Show in folder"
    private bool IsBusy => _scanCts != null || _opCts != null;

    private CancellationTokenSource? _scanCts;
    private S3Profile? _scanning;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var p in _profileStore.Load())
        {
            SetSummary(p, _cache.GetScanInfo(p.Id));
            _profiles.Add(p);
        }
        ProfileList.ItemsSource = _profiles;

        if (_profiles.Count > 0) ProfileList.SelectedIndex = 0;
        else ShowEmpty(NoProfileText);
        UpdateButtons();
        UpdateSortGlyphs();
        if (_current == null) SetPath(null);
    }

    private const string NoProfileText = "No S3 connections yet.\nClick the + button in the bottom-left corner to add one.";

    // ===================== Profiles =====================

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileDialog(null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        dlg.Result.Summary = "Not scanned";
        _profiles.Add(dlg.Result);
        _profileStore.Save(_profiles);
        ProfileList.SelectedItem = dlg.Result; // auto-scans the first time
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not S3Profile p) return;
        if (_scanning?.Id == p.Id)
        {
            MessageBox.Show(this, "This profile is being scanned. Cancel the scan before editing it.", "S3 Explorer");
            return;
        }

        var dlg = new ProfileDialog(p) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        bool scopeChanged = p.ScopeDiffers(dlg.Result);
        p.CopyFrom(dlg.Result);
        _profileStore.Save(_profiles);

        if (scopeChanged)
        {
            _cache.DeleteProfile(p.Id); // different bucket/prefix → old cache no longer valid
            p.Summary = p.ScanTime = "";
        }
        ProfileList.Items.Refresh();

        if (_current?.Id == p.Id)
        {
            if (scopeChanged) { _back.Clear(); _ = OpenProfileAsync(p); }
            else ShowFolder(_currentPath, push: false);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not S3Profile p) return;
        if (_scanning?.Id == p.Id)
        {
            MessageBox.Show(this, "This profile is being scanned. Cancel the scan before deleting it.", "S3 Explorer");
            return;
        }
        if (MessageBox.Show(this, $"Delete profile \"{p.Name}\" and its cache?\n(Data on S3 is not affected)",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _cache.DeleteProfile(p.Id);
        _profiles.Remove(p);
        _profileStore.Save(_profiles);
        if (_profiles.Count == 0) ShowEmpty(NoProfileText);
    }

    private async void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (ProfileList.SelectedItem is not S3Profile p)
        {
            _current = null;
            FileList.ItemsSource = null;
            SetPath(null);
            FolderStatus.Text = SelectionStatus.Text = CacheStatus.Text = "";
            return;
        }
        if (p == _current) return;
        _back.Clear();
        FilterBox.Text = "";
        await OpenProfileAsync(p);
    }

    /// <summary>Open a profile: show it immediately if cached, otherwise run the first scan.</summary>
    private async Task OpenProfileAsync(S3Profile p)
    {
        _current = p;
        _currentPath = "";

        if (_cache.GetScanInfo(p.Id) != null)
        {
            ShowFolder("", push: false);
            return;
        }

        FileList.ItemsSource = null;
        SetPath("");
        FolderStatus.Text = SelectionStatus.Text = CacheStatus.Text = "";

        if (_scanCts != null)
        {
            ShowEmpty($"No cached data for \"{p.Name}\" yet.\nScanning \"{_scanning?.Name}\" — click \"Rescan\" when it finishes.");
            return;
        }

        ShowEmpty("Loading listing for the first time…");
        await ScanAsync(p);
    }

    // ===================== S3 scan =====================

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) await ScanAsync(_current);
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) { _scanCts?.Cancel(); _opCts?.Cancel(); }

    private async Task ScanAsync(S3Profile p)
    {
        if (IsBusy) return;

        _scanCts = new CancellationTokenSource();
        _scanning = p;
        SetScanning(true);
        ProgressText.Text = $"Scanning {p.Name}…";

        var progress = new Progress<ScanProgress>(sp =>
            ProgressText.Text = $"Scanning {p.Name}: {sp.Files:N0} files · {FormatUtil.Size(sp.Bytes)}");

        try
        {
            var token = _scanCts.Token;
            var info = await Task.Run(() => Scanner.ScanAsync(p, _cache, progress, token));
            SetSummary(p, info);
            ProfileList.Items.Refresh();

            if (_current?.Id == p.Id)
            {
                var path = _currentPath.Length > 0 && _cache.GetFolder(p.Id, _currentPath) != null ? _currentPath : "";
                ShowFolder(path, push: false);
            }
        }
        catch (OperationCanceledException)
        {
            if (_current?.Id == p.Id && _cache.GetScanInfo(p.Id) == null)
                ShowEmpty("Scan cancelled. Click \"Rescan\" to try again.");
        }
        catch (Exception ex)
        {
            if (_current?.Id == p.Id && _cache.GetScanInfo(p.Id) == null)
                ShowEmpty("Could not load the listing. Check the profile settings.");
            MessageBox.Show(this, $"Error scanning \"{p.Name}\":\n{ex.Message}", "S3 error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            _scanning = null;
            SetScanning(false);
        }
    }

    private void SetScanning(bool on)
    {
        ProgressPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CancelScanButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    // ===================== Explorer view =====================

    private void ShowFolder(string path, bool push = true)
    {
        if (_current == null) return;
        if (push && path != _currentPath) _back.Push(_currentPath);
        _currentPath = path;

        var entries = _cache.GetChildren(_current.Id, path);
        foreach (var en in entries)
        {
            var (type, _) = en.IsFolder ? ShellInfo.Folder() : ShellInfo.ForExtension(en.Extension);
            en.TypeName = type;
            en.Icon = en.IsFolder ? MacIcons.Folder : MacIcons.ForExtension(en.Extension);
            if (en.LocalPath != null && !LocalCopyValid(en.LocalPath, en.Size)) en.LocalPath = null;
        }

        _view = new ListCollectionView(entries)
        {
            CustomSort = new EntryComparer(_sortKey, _sortAsc),
            Filter = FilterEntry
        };
        FileList.ItemsSource = _view;
        if (_view.Count > 0) FileList.ScrollIntoView(_view.GetItemAt(0));

        SetPath(path);
        ShowEmpty(entries.Count == 0 ? "This folder is empty." : null);

        // Status bar
        int folders = entries.Count(x => x.IsFolder);
        int files = entries.Count - folders;
        long directSize = entries.Where(x => !x.IsFolder).Sum(x => x.Size);
        var agg = _cache.GetFolder(_current.Id, path);
        FolderStatus.Text = $"{folders:N0} folders, {files:N0} files ({FormatUtil.Size(directSize)})" +
                            (agg != null ? $"   •   Total incl. subfolders: {agg.FileCount:N0} files, {FormatUtil.Size(agg.TotalSize)}" : "");

        var scan = _cache.GetScanInfo(_current.Id);
        CacheStatus.Text = scan == null ? "" :
            $"Cache: scanned {scan.ScannedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} (took {FormatUtil.Duration(scan.DurationMs)})";

        UpdateSelectionStatus();
        UpdateButtons();
    }

    private bool FilterEntry(object o)
    {
        var f = FilterBox.Text.Trim();
        return f.Length == 0 || ((S3Entry)o).Name.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string key } || _view == null) return;
        _sortAsc = key == _sortKey ? !_sortAsc : key != "Modified" && key != "Size" && key != "Count";
        _sortKey = key;
        _view.CustomSort = new EntryComparer(_sortKey, _sortAsc);
        UpdateSortGlyphs();
    }

    private static readonly Dictionary<string, string> HeaderTitles = new()
    {
        ["Name"] = "Name", ["Modified"] = "Date modified", ["Size"] = "Size",
        ["Count"] = "Files", ["Type"] = "Type", ["Storage"] = "Storage class"
    };

    private void UpdateSortGlyphs()
    {
        if (FileList.View is not GridView gv) return;
        foreach (var col in gv.Columns)
            if (col.Header is GridViewColumnHeader { Tag: string k } h && HeaderTitles.TryGetValue(k, out var title))
                h.Content = k == _sortKey ? $"{title}  {(_sortAsc ? "\u2191" : "\u2193")}" : title;
    }

    // ===================== Path bar =====================

    public sealed record PathSegment(string Name, string Path, ImageSource Icon, Visibility ChevronVisibility);

    /// <summary>Update the title, s3:// path and breadcrumb bar. path = null when there is no profile.</summary>
    private void SetPath(string? path)
    {
        if (_current == null || path == null)
        {
            FolderTitle.Text = "S3 Explorer";
            PathBox.Text = "";
            PathBar.ItemsSource = null;
            return;
        }

        PathBox.Text = $"s3://{_current.Bucket}/{_current.NormalizedPrefix}{path}";
        var root = (_current.Bucket + "/" + _current.NormalizedPrefix).TrimEnd('/');
        var segs = new List<PathSegment> { new(root, "", MacIcons.Cloud, Visibility.Collapsed) };
        var acc = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += part + "/";
            segs.Add(new PathSegment(part, acc, MacIcons.Folder, Visibility.Visible));
        }
        PathBar.ItemsSource = segs;
        FolderTitle.Text = segs[^1].Name;
    }

    private void PathSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && path != _currentPath)
        {
            FilterBox.Text = "";
            ShowFolder(path);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count > 0) ShowFolder(_back.Pop(), push: false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath.Length > 0) ShowFolder(CacheStore.ParentOf(_currentPath));
    }

    private void OpenSelected()
    {
        if (FileList.SelectedItem is S3Entry { IsFolder: true } en)
        {
            FilterBox.Text = "";
            ShowFolder(en.RelativePath);
        }
        else if (FileList.SelectedItem is S3Entry { IsFolder: false } file)
        {
            // Already downloaded → open the local copy; only download files not yet present
            var files = SelectedEntries().Where(x => !x.IsFolder).ToList();
            if (files.Count == 1 && TryOpenLocal(file)) return;
            var missing = files.Where(x => !TryKeepLocal(x)).ToList();
            if (missing.Count > 0) _ = DownloadAsync(missing, DefaultDownloadDir());
            else TryOpenLocal(file);
        }
    }

    private static bool LocalCopyValid(string path, long size)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length == size;
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether the downloaded copy is still usable; if it was deleted/modified, clear the mark.
    /// If there is no record but Downloads already has a file with the same name and size (downloaded earlier), adopt that file.
    /// </summary>
    private bool TryKeepLocal(S3Entry en)
    {
        if (en.LocalPath == null && _current != null)
        {
            var guess = Path.Combine(DefaultDownloadDir(), SafeName(en.Name));
            if (!LocalCopyValid(guess, en.Size)) return false;
            MarkDownloaded(_current, [new DownloadRecord(en.RelativePath, guess, en.Size, en.LastModifiedUtc?.Ticks ?? 0)]);
            en.LocalPath = guess;
            return true;
        }
        if (en.LocalPath == null) return false;
        if (LocalCopyValid(en.LocalPath, en.Size)) return true;
        en.LocalPath = null;
        if (_current != null) _cache.ForgetDownload(_current.Id, en.RelativePath);
        return false;
    }

    private bool TryOpenLocal(S3Entry en)
    {
        if (!TryKeepLocal(en)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(en.LocalPath!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice($"Could not open \"{en.LocalPath}\": {ex.Message}", en.LocalPath, error: true);
        }
        return true;
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem)
            OpenSelected();
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Back) { Up_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { _ = DeleteSelectedAsync(); e.Handled = true; }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && RescanButton.IsEnabled) { Rescan_Click(sender, e); e.Handled = true; }
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { Back_Click(sender, e); e.Handled = true; }
        else if (e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt) { Up_Click(sender, e); e.Handled = true; }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateSelectionStatus(); UpdateButtons(); }

    private void UpdateSelectionStatus()
    {
        var sel = FileList.SelectedItems.Cast<S3Entry>().ToList();
        SelectionStatus.Text = sel.Count == 0 ? "" :
            $"{sel.Count:N0} selected — {FormatUtil.Size(sel.Sum(x => x.Size))}";
    }

    // ===================== Download / Delete =====================

    private List<S3Entry> SelectedEntries() => FileList.SelectedItems.Cast<S3Entry>().ToList();

    private static string DefaultDownloadDir()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private void Download_Click(object sender, RoutedEventArgs e) => _ = DownloadAsync(SelectedEntries(), DefaultDownloadDir());

    private void DownloadTo_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedEntries();
        if (items.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose destination folder", InitialDirectory = DefaultDownloadDir() };
        if (dlg.ShowDialog(this) == true) _ = DownloadAsync(items, dlg.FolderName);
    }

    private void DeleteItems_Click(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync();

    private async Task DownloadAsync(IReadOnlyList<S3Entry> items, string destDir)
    {
        if (_current == null || items.Count == 0) return;
        if (IsBusy) { ShowNotice("Another operation is running — please wait or click Cancel.", null, error: true); return; }

        var p = _current;
        var bucket = p.Bucket.Trim();
        var prefix = p.NormalizedPrefix;
        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        HideNotice();
        ProgressText.Text = "Preparing download…";
        SetScanning(true);

        long doneBytes = 0, totalBytes = 0;
        int doneFiles = 0, totalFiles = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => ProgressText.Text = totalFiles == 0 ? "Listing files…" :
            $"Downloading {Volatile.Read(ref doneFiles):N0}/{totalFiles:N0} files · " +
            $"{FormatUtil.Size(Interlocked.Read(ref doneBytes))} / {FormatUtil.Size(totalBytes)}";
        timer.Start();

        string? reveal = null;
        var downloaded = new System.Collections.Concurrent.ConcurrentBag<DownloadRecord>();
        try
        {
            var failed = await Task.Run(async () =>
            {
                using var client = S3Service.CreateClient(p);
                var jobs = new List<(string Key, string Local, long Size, long Ticks)>();
                foreach (var en in items)
                {
                    if (!en.IsFolder)
                    {
                        jobs.Add((prefix + en.RelativePath, UniquePath(Path.Combine(destDir, SafeName(en.Name))), en.Size,
                                  en.LastModifiedUtc?.Ticks ?? 0));
                        continue;
                    }
                    var folderKey = prefix + en.RelativePath;
                    var root = UniqueDir(Path.Combine(destDir, SafeName(en.Name)));
                    await foreach (var page in S3Service.ListAllAsync(client, bucket, folderKey, ct))
                        foreach (var o in page)
                        {
                            if (o.Key.EndsWith('/')) continue;
                            var local = SafeCombine(root, o.Key[folderKey.Length..]);
                            if (local != null) jobs.Add((o.Key, local, o.Size, o.LastModifiedTicksUtc));
                        }
                }

                totalBytes = jobs.Sum(j => j.Size);
                Volatile.Write(ref totalFiles, jobs.Count);
                reveal = jobs.Count == 1 && items.Count == 1 && !items[0].IsFolder ? jobs[0].Local
                       : items.Count == 1 && items[0].IsFolder ? Path.Combine(destDir, SafeName(items[0].Name)) : destDir;

                var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
                await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (j, token) =>
                    {
                        try
                        {
                            await S3Service.DownloadAsync(client, bucket, j.Key, j.Local, n => Interlocked.Add(ref doneBytes, n), token);
                            downloaded.Add(new DownloadRecord(j.Key[prefix.Length..], j.Local, j.Size, j.Ticks));
                            Interlocked.Increment(ref doneFiles);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { errors.Add($"{j.Key}: {ex.Message}"); }
                    });
                return errors.ToList();
            });

            if (failed.Count == 0)
                ShowNotice($"Downloaded {doneFiles:N0} files ({FormatUtil.Size(doneBytes)}) to {destDir}", reveal);
            else
                ShowNotice($"Downloaded {doneFiles:N0}/{totalFiles:N0} files — {failed.Count:N0} failed. First error: {failed[0]}", reveal, error: true);
        }
        catch (OperationCanceledException)
        {
            ShowNotice($"Download cancelled ({doneFiles:N0}/{totalFiles:N0} files completed).", reveal, error: true);
        }
        catch (Exception ex)
        {
            ShowNotice("Download error: " + ex.Message, null, error: true);
        }
        finally
        {
            timer.Stop();
            _opCts.Dispose();
            _opCts = null;
            SetScanning(false);
            MarkDownloaded(p, downloaded.ToList());
        }
    }

    /// <summary>Remember downloaded files and update the marks on the rows currently shown.</summary>
    private void MarkDownloaded(S3Profile p, List<DownloadRecord> records)
    {
        if (records.Count == 0) return;
        try { _cache.RecordDownloads(p.Id, records); }
        catch { /* only the mark is lost, the file is still downloaded */ }

        if (_current?.Id != p.Id || _view == null) return;
        var byKey = records.ToDictionary(r => r.Key, r => r.LocalPath, StringComparer.Ordinal);
        foreach (S3Entry en in _view.SourceCollection)
            if (!en.IsFolder && byKey.TryGetValue(en.RelativePath, out var local))
                en.LocalPath = local;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_current == null) return;
        var items = SelectedEntries();
        if (items.Count == 0) return;
        if (IsBusy) { ShowNotice("Another operation is running — please wait or click Cancel.", null, error: true); return; }

        long files = items.Sum(x => x.IsFolder ? x.FileCount : 1);
        long size = items.Sum(x => x.Size);
        string what = items.Count == 1
            ? (items[0].IsFolder ? $"folder \"{items[0].Name}\" ({files:N0} files inside)" : $"\"{items[0].Name}\"")
            : $"{items.Count:N0} items ({files:N0} files)";
        if (MessageBox.Show(this,
                $"Permanently delete {what} — {FormatUtil.Size(size)} from S3?\n\nBucket: {_current.Bucket}\nThis action CANNOT be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var p = _current;
        var bucket = p.Bucket.Trim();
        var prefix = p.NormalizedPrefix;
        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        HideNotice();
        ProgressText.Text = "Listing objects to delete…";
        SetScanning(true);

        int deleted = 0, total = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { if (total > 0) ProgressText.Text = $"Deleting {Volatile.Read(ref deleted):N0}/{total:N0} objects…"; };
        timer.Start();

        try
        {
            var failed = await Task.Run(async () =>
            {
                using var client = S3Service.CreateClient(p);
                var keys = new List<string>();
                foreach (var en in items)
                {
                    var full = prefix + en.RelativePath;
                    if (!en.IsFolder) { keys.Add(full); continue; }
                    await foreach (var page in S3Service.ListAllAsync(client, bucket, full, ct))
                        keys.AddRange(page.Select(o => o.Key));
                    if (!keys.Contains(full)) keys.Add(full); // folder marker (if any)
                }
                Volatile.Write(ref total, keys.Count);

                var bad = await S3Service.DeleteKeysAsync(client, bucket, keys, n => Volatile.Write(ref deleted, n), ct);
                var badSet = new HashSet<string>(bad, StringComparer.Ordinal);

                // Only update the cache for what was deleted successfully
                var okFiles = items.Where(x => !x.IsFolder && !badSet.Contains(prefix + x.RelativePath)).Select(x => x.RelativePath).ToList();
                var okFolders = items.Where(x => x.IsFolder && !bad.Any(k => k.StartsWith(prefix + x.RelativePath, StringComparison.Ordinal)))
                                     .Select(x => x.RelativePath).ToList();
                _cache.ApplyDelete(p.Id, okFiles, okFolders);
                // a missing empty-folder marker on S3 doesn't count as an error
                return bad.Where(k => !k.EndsWith('/')).ToList();
            });

            if (failed.Count == 0)
                ShowNotice($"Deleted {what} from S3.", null);
            else
                ShowNotice($"Could not delete {failed.Count:N0} objects (e.g. {failed[0]}). Check the s3:DeleteObject permission, then Rescan.", null, error: true);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Delete cancelled midway — some items may already be deleted. Click \"Rescan\" to refresh.", null, error: true);
        }
        catch (Exception ex)
        {
            ShowNotice("Delete error: " + ex.Message, null, error: true);
        }
        finally
        {
            timer.Stop();
            _opCts.Dispose();
            _opCts = null;
            SetScanning(false);
            SetSummary(p, _cache.GetScanInfo(p.Id));
            ProfileList.Items.Refresh();
            if (_current?.Id == p.Id)
            {
                var path = _currentPath.Length == 0 || _cache.GetFolder(p.Id, _currentPath) != null ? _currentPath : "";
                ShowFolder(path, push: false);
            }
        }
    }

    // ---------- Notices ----------

    private void ShowNotice(string text, string? revealPath, bool error = false)
    {
        NoticeText.Text = text;
        NoticeText.ToolTip = text;
        _noticePath = revealPath;
        NoticeOpenButton.Visibility = revealPath != null && (File.Exists(revealPath) || Directory.Exists(revealPath))
            ? Visibility.Visible : Visibility.Collapsed;
        NoticePanel.Background = new SolidColorBrush(error ? Color.FromRgb(0xFF, 0xF2, 0xF0) : Color.FromRgb(0xEF, 0xF8, 0xF1));
        NoticePanel.Visibility = Visibility.Visible;
    }

    private void HideNotice() => NoticePanel.Visibility = Visibility.Collapsed;

    private void CloseNotice_Click(object sender, RoutedEventArgs e) => HideNotice();

    private void NoticeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_noticePath == null) return;
        try
        {
            if (File.Exists(_noticePath))
                Process.Start("explorer.exe", $"/select,\"{_noticePath}\"");
            else if (Directory.Exists(_noticePath))
                Process.Start("explorer.exe", $"\"{_noticePath}\"");
        }
        catch { /* ignore */ }
    }

    // ---------- Safe local paths ----------

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 || s == ".." ? "_" : s;
    }

    /// <summary>Join an S3-style relative path onto root, preventing escape outside root.</summary>
    private static string? SafeCombine(string root, string rel)
    {
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName).ToArray();
        if (parts.Length == 0) return null;
        var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    private static string UniqueDir(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        for (int i = 1; ; i++)
        {
            var p = $"{path} ({i})";
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    // ===================== Context menu =====================

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is S3Entry { IsFolder: true }) OpenSelected();
    }

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var en = FileList.SelectedItem as S3Entry;
        bool local = en is { IsFolder: false } && TryKeepLocal(en);
        OpenFolderMenu.Visibility = en is { IsFolder: true } ? Visibility.Visible : Visibility.Collapsed;
        OpenLocalMenu.Visibility = RevealLocalMenu.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is S3Entry { IsFolder: false } en) TryOpenLocal(en);
    }

    private void RevealLocal_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not S3Entry { IsFolder: false } en || !TryKeepLocal(en)) return;
        try { Process.Start("explorer.exe", $"/select,\"{en.LocalPath}\""); }
        catch { /* ignore */ }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) =>
        CopySelected(en => en.Name);

    private void CopyKey_Click(object sender, RoutedEventArgs e) =>
        CopySelected(en => _current!.NormalizedPrefix + en.RelativePath);

    private void CopyUri_Click(object sender, RoutedEventArgs e) =>
        CopySelected(en => $"s3://{_current!.Bucket}/{_current.NormalizedPrefix}{en.RelativePath}");

    private void CopySelected(Func<S3Entry, string> f)
    {
        if (_current == null) return;
        var lines = FileList.SelectedItems.Cast<S3Entry>().Select(f).ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    // ===================== Helpers =====================

    private void ShowEmpty(string? text)
    {
        EmptyText.Text = text ?? "";
        EmptyPanel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateButtons()
    {
        bool has = ProfileList.SelectedItem != null;
        EditButton.IsEnabled = DeleteButton.IsEnabled = has;
        RescanButton.IsEnabled = has && !IsBusy;
        bool sel = FileList.SelectedItems.Count > 0 && !IsBusy;
        DownloadFileButton.IsEnabled = DeleteFileButton.IsEnabled = sel;
        BackButton.IsEnabled = _back.Count > 0;
        UpButton.IsEnabled = _currentPath.Length > 0;
    }

    private static void SetSummary(S3Profile p, ScanInfo? s)
    {
        p.Summary = s == null ? "Not scanned" : $"{s.FileCount:N0} files · {FormatUtil.Size(s.TotalSize)}";
        p.ScanTime = s == null ? "" : s.ScannedAtUtc.ToLocalTime().ToString("dd/MM HH:mm");
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { _scanCts?.Cancel(); _opCts?.Cancel(); }

    /// <summary>Folders always come first; names compare Explorer-style.</summary>
    private sealed class EntryComparer : IComparer
    {
        private readonly string _key;
        private readonly bool _asc;

        public EntryComparer(string key, bool asc) { _key = key; _asc = asc; }

        public int Compare(object? x, object? y)
        {
            var a = (S3Entry)x!;
            var b = (S3Entry)y!;
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;

            int r = _key switch
            {
                "Size" => a.Size.CompareTo(b.Size),
                "Count" => a.FileCount.CompareTo(b.FileCount),
                "Modified" => Nullable.Compare(a.LastModifiedUtc, b.LastModifiedUtc),
                "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                "Storage" => string.Compare(a.StorageClass, b.StorageClass, StringComparison.OrdinalIgnoreCase),
                _ => 0
            };
            if (r == 0) r = FormatUtil.NaturalCompare(a.Name, b.Name);
            return _asc ? r : -r;
        }
    }
}
