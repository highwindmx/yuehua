using System.Collections.Generic;
using Avalonia.Media.Imaging;
using ImageMagick;

namespace Yuehua.Services;

/// <summary>一次成功解码后的图像数据：Full 用于导出（原图），Display 用于屏幕渲染（按上限降采样）。</summary>
public class LoadedImage
{
    public string Path { get; set; } = "";
    public MagickImage Full { get; set; } = null!;
    public Bitmap Display { get; set; } = null!;
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public string Format { get; set; } = "";
    public long FileSize { get; set; }
    /// <summary>解码时文件的签名（修改时间 + 大小），供 ImageCache 判断同名文件是否被覆盖重写。</summary>
    public long FileStamp { get; set; }
    /// <summary>容器内帧数（ICO 等多帧格式）；单帧图片为 1。</summary>
    public int FrameCount { get; set; } = 1;
    public Dictionary<string, string> Exif { get; set; } = new();
}

public static class ImageLoader
{
    /// <summary>解码文件。displayMax 为屏幕位图长边上限，避免超大图卡顿。
    /// ICO 含多分辨率帧，挑最大的一帧用于查看（否则会被当成 16×16 缩略）。</summary>
    public static LoadedImage Load(string path, int displayMax = 2600)
    {
        var (img, frameCount) = ReadBestFrame(path);
        var ext = Path.GetExtension(path).TrimStart('.');
        var info = new LoadedImage
        {
            Path = path,
            Full = img,
            OriginalWidth = (int)img.Width,
            OriginalHeight = (int)img.Height,
            // ICO 这类多帧容器解码出的帧 Format 是 Png，直接显示会误导（状态栏写成「Png」）；
            // 以文件真实扩展名作为格式名。
            Format = ext.Length > 0 ? ext.ToUpperInvariant() : img.Format.ToString().ToUpperInvariant(),
            FileSize = new FileInfo(path).Length,
            FileStamp = StampOf(path),
            FrameCount = frameCount,
        };

        MagickImage disp = img;
        var dw = (int)img.Width;
        var dh = (int)img.Height;
        if (img.Width > (uint)displayMax || img.Height > (uint)displayMax)
        {
            disp = (MagickImage)img.Clone();
            var ratio = (double)displayMax / Math.Max(img.Width, img.Height);
            dw = (int)(img.Width * ratio);
            dh = (int)(img.Height * ratio);
            disp.Resize((uint)dw, (uint)dh);
        }

        var bytes = disp.ToByteArray(MagickFormat.Png);
        info.Display = new Bitmap(new MemoryStream(bytes));
        info.DisplayWidth = dw;
        info.DisplayHeight = dh;
        if (disp != img) disp.Dispose();

        info.Exif = ExtractExif(img);
        return info;
    }

    /// <summary>生成小尺寸缩略图位图（不保留 Full，仅供缩略图条使用）。</summary>
    /// <summary>文件签名：修改时间 + 大小。用于判断同名文件是否被覆盖重写（缓存失效）。</summary>
    public static long StampOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks ^ (fi.Length * 397L);
        }
        catch { return 0; }
    }

    public static Bitmap LoadThumbnail(string path, int max = 160)
    {
        var result = ReadBestFrame(path);
        using var img = result.Image;
        var ratio = (double)max / Math.Max(img.Width, img.Height);
        if (ratio < 1)
            img.Resize((uint)(img.Width * ratio), (uint)(img.Height * ratio));
        var bytes = img.ToByteArray(MagickFormat.Png);
        return new Bitmap(new MemoryStream(bytes));
    }

    /// <summary>读取文件首帧；ICO 则挑选面积最大的一帧（避免把 16×16 当作主图），并返回容器内帧数。</summary>
    private static (MagickImage Image, int FrameCount) ReadBestFrame(string path)
    {
        if (!Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return (new MagickImage(path), 1);

        using var col = new MagickImageCollection(path);
        IMagickImage<byte>? best = null;
        foreach (var m in col)
        {
            if (best == null || (long)m.Width * m.Height > (long)best.Width * best.Height)
                best = m;
        }
        // best 仍属集合，必须 Clone 出来再离开 using（集合 Dispose 会使原帧失效）
        return best != null
            ? ((MagickImage)best.Clone(), col.Count)
            : (new MagickImage(path), 1);
    }

    private static Dictionary<string, string> ExtractExif(MagickImage img)
    {
        var d = new Dictionary<string, string>();
        try
        {
            var p = img.GetExifProfile();
            if (p != null)
            {
                foreach (var v in p.Values)
                {
                    if (v != null)
                    {
                        var s = v.ToString();
                        if (!string.IsNullOrEmpty(s))
                            d[v.Tag.ToString()] = s;
                    }
                }
            }
        }
        catch
        {
            // EXIF 解析失败不阻塞看图
        }
        return d;
    }
}
