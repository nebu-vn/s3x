using System.Windows;
using S3Explorer.Models;

namespace S3Explorer;

/// <summary>Choose where to copy / move the selected items: destination connection, folder, overwrite.</summary>
public partial class TransferDialog : Window
{
    private readonly Func<S3Profile, string, string?> _validate;

    public S3Profile? Destination { get; private set; }

    /// <summary>Folder relative to the destination's prefix: no leading '/', ends with '/' (or empty).</summary>
    public string Folder { get; private set; } = "";

    public bool Overwrite { get; private set; }

    /// <param name="validate">Returns an error message for (destination, folder), or null if it is allowed.</param>
    public TransferDialog(bool move, string summary, string source, IEnumerable<S3Profile> profiles,
        S3Profile current, string currentPath, Func<S3Profile, string, string?> validate)
    {
        InitializeComponent();
        _validate = validate;

        Title = move ? "Move to another bucket" : "Copy to another bucket";
        OkButton.Content = move ? "Move" : "Copy";
        SummaryText.Text = summary;
        SourceText.Text = "From: " + source;

        var list = profiles.ToList();
        ProfileBox.ItemsSource = list;
        var other = list.FirstOrDefault(p => p.Id != current.Id);
        ProfileBox.SelectedItem = other ?? current;
        FolderBox.Text = other == null ? currentPath : "";

        Loaded += (_, _) => { FolderBox.Focus(); FolderBox.CaretIndex = FolderBox.Text.Length; };
    }

    private static string NormalizeFolder(string s)
    {
        var f = s.Trim().Replace('\\', '/').TrimStart('/');
        return f.Length == 0 || f.EndsWith('/') ? f : f + "/";
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || ProfileBox.SelectedItem is not S3Profile p) return;
        var folder = NormalizeFolder(FolderBox.Text);
        TargetText.Text = $"To: s3://{p.Bucket.Trim()}/{p.NormalizedPrefix}{folder}";
        ErrorText.Text = _validate(p, folder) ?? "";
        ErrorText.ToolTip = ErrorText.Text;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not S3Profile p) return;
        var folder = NormalizeFolder(FolderBox.Text);
        if (folder.Split('/').SkipLast(1).Any(s => s.Length == 0))
        {
            ErrorText.Text = "The folder path contains an empty segment (\"//\").";
            return;
        }
        var err = _validate(p, folder);
        if (err != null) { ErrorText.Text = err; return; }

        Destination = p;
        Folder = folder;
        Overwrite = OverwriteBox.IsChecked == true;
        DialogResult = true;
    }
}
