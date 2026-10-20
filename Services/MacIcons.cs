using System.Windows;
using System.Windows.Media;

namespace S3Explorer.Services;

/// <summary>
/// Icon vector phong cách macOS (thư mục xanh, tài liệu trang trắng gập góc, màu theo loại file).
/// Vẽ bằng DrawingImage nên nét ở mọi kích thước / DPI. Mọi hình nằm trong khung 64×64.
/// </summary>
public static class MacIcons
{
    public static ImageSource Folder { get; } = BuildFolder();
    public static ImageSource Cloud { get; } = BuildCloud();

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    private enum Kind { Plain, Lines, Band, Image, Video }

    public static ImageSource ForExtension(string? ext)
    {
        ext = (ext ?? "").TrimStart('.').ToLowerInvariant();
        if (Cache.TryGetValue(ext, out var hit)) return hit;
        var (kind, color) = Classify(ext);
        return Cache[ext] = BuildFile(kind, color);
    }

    private static (Kind, string) Classify(string ext) => ext switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "tif" or "tiff" or "svg" or "heic" or "heif" or "ico" or "avif" or "raw" or "psd"
            => (Kind.Image, ""),
        "mp4" or "mov" or "avi" or "mkv" or "webm" or "m4v" or "wmv" or "flv" or "3gp" or "ts" or "m3u8"
            => (Kind.Video, "#FF375F"),
        "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "opus" or "wma"
            => (Kind.Band, "#FF9F0A"),
        "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "zst"
            => (Kind.Band, "#8E8E93"),
        "pdf" => (Kind.Band, "#FF3B30"),
        "doc" or "docx" or "rtf" or "odt" or "pages" => (Kind.Band, "#0A84FF"),
        "xls" or "xlsx" or "csv" or "tsv" or "ods" or "numbers" or "parquet" => (Kind.Band, "#30B158"),
        "ppt" or "pptx" or "odp" or "key" => (Kind.Band, "#F56A00"),
        "js" or "mjs" or "jsx" or "tsx" or "json" or "xml" or "html" or "htm" or "css" or "scss" or "cs" or "py" or "go"
            or "java" or "kt" or "c" or "cpp" or "h" or "hpp" or "rs" or "rb" or "php" or "sh" or "ps1" or "bat"
            or "yml" or "yaml" or "toml" or "ini" or "sql" or "vue" or "swift"
            => (Kind.Lines, "#5E5CE6"),
        "txt" or "md" or "log" or "conf" or "cfg" or "env"
            => (Kind.Lines, "#AEAEB2"),
        "" => (Kind.Plain, ""),
        _ => (Kind.Band, "#C7C7CC")
    };

    // ---------- Thư mục ----------
    private static ImageSource BuildFolder()
    {
        var g = Box();
        var back = Geometry.Parse(
            "M4,15 C4,12.2 6.2,10 9,10 L23,10 C24.7,10 26,10.7 27,12 L29,14.5 L55,14.5 C57.8,14.5 60,16.7 60,19.5 L60,49 C60,51.8 57.8,54 55,54 L9,54 C6.2,54 4,51.8 4,49 Z");
        g.Children.Add(new GeometryDrawing(Lin("#5EB4F6", "#3C95E6"), MkPen("#2E85D6", 0.8), back));
        var front = new RectangleGeometry(new Rect(4, 20.5, 56, 33.5), 4.5, 4.5);
        g.Children.Add(new GeometryDrawing(Lin("#A9DCFF", "#6DBFF8"), MkPen("#4FA5EB", 0.8), front));
        g.Children.Add(new GeometryDrawing(null, MkPen("#D4EEFF", 1.2), new LineGeometry(new Point(8, 22.2), new Point(56, 22.2))));
        return Make(g);
    }

    // ---------- Đám mây (cho kết nối S3) ----------
    private static ImageSource BuildCloud()
    {
        var g = Box();
        Geometry c = new EllipseGeometry(new Point(23, 37), 11, 11);
        c = Geometry.Combine(c, new EllipseGeometry(new Point(35, 29), 14, 14), GeometryCombineMode.Union, null);
        c = Geometry.Combine(c, new EllipseGeometry(new Point(46, 38), 10, 10), GeometryCombineMode.Union, null);
        c = Geometry.Combine(c, new RectangleGeometry(new Rect(13, 36, 43, 12), 6, 6), GeometryCombineMode.Union, null);
        g.Children.Add(new GeometryDrawing(Lin("#9AD6FF", "#3D9AEE"), MkPen("#2E85D6", 0.9), c));
        return Make(g);
    }

    // ---------- Tài liệu ----------
    private static ImageSource BuildFile(Kind kind, string color)
    {
        var g = Box();
        var page = Geometry.Parse("M13,3 L41,3 L54,16 L54,58 C54,59.7 52.7,61 51,61 L13,61 C11.3,61 10,59.7 10,58 L10,6 C10,4.3 11.3,3 13,3 Z");
        g.Children.Add(new GeometryDrawing(Lin("#FFFFFF", "#F1F1F4"), MkPen("#A3A3AA", 1.6), page));
        var fold = Geometry.Parse("M41,3 L41,13 C41,14.7 42.3,16 44,16 L54,16 Z");
        g.Children.Add(new GeometryDrawing(MkBrush("#E4E4E9"), MkPen("#A3A3AA", 1.6), fold));

        switch (kind)
        {
            case Kind.Lines:
                var p = MkPen(color, 3);
                p.StartLineCap = p.EndLineCap = PenLineCap.Round;
                int i = 0;
                foreach (var y in new double[] { 24, 30.5, 37, 43.5, 50 })
                {
                    double end = i++ % 3 == 2 ? 38 : 48;
                    g.Children.Add(new GeometryDrawing(null, p, new LineGeometry(new Point(16, y), new Point(end, y))));
                }
                break;

            case Kind.Band:
            case Kind.Video:
                g.Children.Add(new GeometryDrawing(MkBrush(color), null, new RectangleGeometry(new Rect(14, 35, 36, 19), 4, 4)));
                if (kind == Kind.Video)
                    g.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry.Parse("M28.5,39.5 L28.5,49.5 L37.5,44.5 Z")));
                break;

            case Kind.Image:
                var frame = new RectangleGeometry(new Rect(15, 22, 34, 31), 3, 3);
                g.Children.Add(new GeometryDrawing(Lin("#8AD8FF", "#4AA6F0"), null, frame));
                var hills = new DrawingGroup { ClipGeometry = frame };
                hills.Children.Add(new GeometryDrawing(MkBrush("#34C759"), null, Geometry.Parse("M13,54 L26,36 L34,46 L40,40 L52,54 Z")));
                hills.Children.Add(new GeometryDrawing(MkBrush("#FFD60A"), null, new EllipseGeometry(new Point(41, 29), 3.6, 3.6)));
                g.Children.Add(hills);
                break;
        }
        return Make(g);
    }

    // ---------- Helpers ----------
    private static DrawingGroup Box()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        return g;
    }

    private static ImageSource Make(DrawingGroup g)
    {
        g.Freeze();
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush MkBrush(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }

    private static Brush Lin(string top, string bottom)
    {
        var b = new LinearGradientBrush(C(top), C(bottom), 90);
        b.Freeze();
        return b;
    }

    private static Pen MkPen(string hex, double w) => new(MkBrush(hex), w) { LineJoin = PenLineJoin.Round };
}
