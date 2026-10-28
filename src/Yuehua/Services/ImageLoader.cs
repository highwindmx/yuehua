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
    public Dictionary<string, string> Exif { get; set; } = new();
}

public static class ImageLoader
{
    /// <summary>解码文件。displayMax 为屏幕位图长边上限，避免超大图卡顿。</summary>
    public static LoadedImage Load(string path, int displayMax = 2600)
    {
        var img = new MagickImage(path);
        var info = new LoadedImage
        {
            Path = path,
            Full = img,
            OriginalWidth = (int)img.Width,
            OriginalHeight = (int)img.Height,
            Format = img.Format.ToString(),
            FileSize = new FileInfo(path).Length,
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
    public static Bitmap LoadThumbnail(string path, int max = 160)
    {
        using var img = new MagickImage(path);
        var ratio = (double)max / Math.Max(img.Width, img.Height);
        if (ratio < 1)
            img.Resize((uint)(img.Width * ratio), (uint)(img.Height * ratio));
        var bytes = img.ToByteArray(MagickFormat.Png);
        return new Bitmap(new MemoryStream(bytes));
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
