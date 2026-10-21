using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows.Data;

namespace S3Explorer.Services;

/// <summary>
/// Sidebar server groups: the group key of an endpoint, and which groups are collapsed
/// (persisted to %LocalAppData%\S3Explorer\collapsed-groups.json so it survives refreshes and restarts).
/// </summary>
public static class SidebarGroups
{
    public const string Aws = "Amazon S3";

    // second-level labels used under 2-letter country TLDs: x.com.vn, x.co.uk, x.or.jp…
    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
        { "com", "net", "org", "gov", "edu", "co", "ac", "or", "ne", "go", "info", "biz", "name" };

    /// <summary>"https://s3.us-west-2.wasabisys.com" → "wasabisys.com", "" → "Amazon S3".</summary>
    public static string ForEndpoint(string? serviceUrl)
    {
        var url = (serviceUrl ?? "").Trim();
        if (url.Length == 0) return Aws;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;

        var host = u.Host.ToLowerInvariant();
        if (IPAddress.TryParse(host, out _)) return host;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int take = parts.Length >= 3 && parts[^1].Length == 2 && SecondLevel.Contains(parts[^2]) ? 3 : 2;
        var domain = parts.Length <= take ? host : string.Join('.', parts[^take..]);
        return domain == "amazonaws.com" ? Aws : domain;
    }

    private static readonly string FilePath = Path.Combine(ProfileStore.AppDataDir, "collapsed-groups.json");
    private static HashSet<string>? _collapsed;

    private static HashSet<string> Collapsed
    {
        get
        {
            if (_collapsed != null) return _collapsed;
            try
            {
                _collapsed = File.Exists(FilePath)
                    ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch { _collapsed = new HashSet<string>(StringComparer.Ordinal); }
            return _collapsed;
        }
    }

    public static bool IsExpanded(string group) => !Collapsed.Contains(group);

    public static void SetExpanded(string group, bool expanded)
    {
        bool changed = expanded ? Collapsed.Remove(group) : Collapsed.Add(group);
        if (!changed) return;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Collapsed.ToList())); }
        catch { /* only the remembered state is lost */ }
    }
}

/// <summary>Group name → whether its header starts expanded.</summary>
public sealed class GroupExpandedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        SidebarGroups.IsExpanded(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
