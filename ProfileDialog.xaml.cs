using System.Windows;
using System.Windows.Media;
using S3Explorer.Models;
using S3Explorer.Services;

namespace S3Explorer;

public partial class ProfileDialog : Window
{
    private readonly S3Profile? _original;

    /// <summary>Result after clicking Save (a copy, not yet written to the list).</summary>
    public S3Profile? Result { get; private set; }

    public ProfileDialog(S3Profile? existing)
    {
        InitializeComponent();
        _original = existing;

        var p = existing ?? new S3Profile();
        Title = existing == null ? "Add S3 profile" : $"Edit profile — {existing.Name}";
        NameBox.Text = p.Name;
        EndpointBox.Text = p.ServiceUrl;
        RegionBox.Text = p.Region;
        BucketBox.Text = p.Bucket;
        PrefixBox.Text = p.Prefix;
        PathStyleBox.IsChecked = p.ForcePathStyle;
        AccessKeyBox.Text = p.AccessKey;

        if (existing != null && !string.IsNullOrEmpty(existing.EncryptedSecretKey))
            SecretHint.Text = "Leave empty to keep the current secret key.";

        Loaded += (_, _) => NameBox.Focus();
    }

    private S3Profile Build()
    {
        var p = _original?.Clone() ?? new S3Profile();
        p.Name = NameBox.Text.Trim();
        p.ServiceUrl = EndpointBox.Text.Trim();
        p.Region = RegionBox.Text.Trim();
        p.Bucket = BucketBox.Text.Trim();
        p.Prefix = PrefixBox.Text.Trim();
        p.ForcePathStyle = PathStyleBox.IsChecked == true;
        p.AccessKey = AccessKeyBox.Text.Trim();
        if (SecretBox.Password.Length > 0) p.SecretKey = SecretBox.Password;
        return p;
    }

    private string? Validate(S3Profile p)
    {
        if (p.Bucket.Length == 0) return "Please enter a bucket name.";
        if (p.ServiceUrl.Length == 0 && p.Region.Length == 0) return "Please enter a region when using AWS S3.";
        if (p.ServiceUrl.Length > 0 && !Uri.TryCreate(p.ServiceUrl, UriKind.Absolute, out _))
            return "Invalid endpoint (expected https://...).";
        if (p.AccessKey.Length > 0 && string.IsNullOrEmpty(p.EncryptedSecretKey))
            return "Please enter the secret key.";
        return null;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var p = Build();
        var err = Validate(p);
        if (err != null) { ShowTest(err, false); return; }

        TestButton.IsEnabled = false;
        ShowTest("Testing…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await S3Service.TestAsync(p, cts.Token);
            ShowTest("Connection successful ✔", true);
        }
        catch (Exception ex)
        {
            ShowTest("Error: " + ex.Message, false);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ShowTest(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.ToolTip = text;
        TestResult.Foreground = ok switch
        {
            true => Brushes.SeaGreen,
            false => Brushes.Firebrick,
            _ => Brushes.Gray
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var p = Build();
        if (p.Name.Length == 0) p.Name = p.Bucket + (p.NormalizedPrefix.Length > 0 ? "/" + p.NormalizedPrefix : "");
        var err = Validate(p);
        if (err != null)
        {
            MessageBox.Show(this, err, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = p;
        DialogResult = true;
    }
}
