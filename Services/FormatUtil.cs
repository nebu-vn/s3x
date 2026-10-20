using System.Runtime.InteropServices;

namespace S3Explorer.Services;

public static class FormatUtil
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {Units[i]}";
    }

    public static string Duration(long ms) =>
        ms < 1000 ? $"{ms} ms" :
        ms < 60_000 ? $"{ms / 1000.0:0.#} giây" :
        $"{ms / 60_000} phút {ms / 1000 % 60} giây";

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>So sánh tên kiểu Windows Explorer (file2 &lt; file10).</summary>
    public static int NaturalCompare(string a, string b)
    {
        try { return StrCmpLogicalW(a, b); }
        catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
    }
}
