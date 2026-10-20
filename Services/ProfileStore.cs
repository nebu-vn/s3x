using System.IO;
using System.Text.Json;
using S3Explorer.Models;

namespace S3Explorer.Services;

/// <summary>Lưu danh sách cấu hình S3 vào %LocalAppData%\S3Explorer\profiles.json.</summary>
public sealed class ProfileStore
{
    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S3Explorer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(AppDataDir, "profiles.json");

    public List<S3Profile> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<S3Profile>>(File.ReadAllText(_path), JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".broken", overwrite: true);
            return new();
        }
    }

    public void Save(IEnumerable<S3Profile> profiles)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profiles.ToList(), JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }
}
