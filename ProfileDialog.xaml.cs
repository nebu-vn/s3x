using System.Windows;
using System.Windows.Media;
using S3Explorer.Models;
using S3Explorer.Services;

namespace S3Explorer;

public partial class ProfileDialog : Window
{
    private readonly S3Profile? _original;

    /// <summary>Kết quả sau khi bấm Lưu (bản sao, chưa ghi vào danh sách).</summary>
    public S3Profile? Result { get; private set; }

    public ProfileDialog(S3Profile? existing)
    {
        InitializeComponent();
        _original = existing;

        var p = existing ?? new S3Profile();
        Title = existing == null ? "Thêm cấu hình S3" : $"Sửa cấu hình — {existing.Name}";
        NameBox.Text = p.Name;
        EndpointBox.Text = p.ServiceUrl;
        RegionBox.Text = p.Region;
        BucketBox.Text = p.Bucket;
        PrefixBox.Text = p.Prefix;
        PathStyleBox.IsChecked = p.ForcePathStyle;
        AccessKeyBox.Text = p.AccessKey;

        if (existing != null && !string.IsNullOrEmpty(existing.EncryptedSecretKey))
            SecretHint.Text = "Để trống để giữ secret key hiện tại.";

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
        if (p.Bucket.Length == 0) return "Vui lòng nhập tên bucket.";
        if (p.ServiceUrl.Length == 0 && p.Region.Length == 0) return "Vui lòng nhập region khi dùng AWS S3.";
        if (p.ServiceUrl.Length > 0 && !Uri.TryCreate(p.ServiceUrl, UriKind.Absolute, out _))
            return "Endpoint không hợp lệ (cần dạng https://...).";
        if (p.AccessKey.Length > 0 && string.IsNullOrEmpty(p.EncryptedSecretKey))
            return "Vui lòng nhập secret key.";
        return null;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var p = Build();
        var err = Validate(p);
        if (err != null) { ShowTest(err, false); return; }

        TestButton.IsEnabled = false;
        ShowTest("Đang kiểm tra…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await S3Service.TestAsync(p, cts.Token);
            ShowTest("Kết nối thành công ✔", true);
        }
        catch (Exception ex)
        {
            ShowTest("Lỗi: " + ex.Message, false);
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
            MessageBox.Show(this, err, "Thiếu thông tin", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = p;
        DialogResult = true;
    }
}
