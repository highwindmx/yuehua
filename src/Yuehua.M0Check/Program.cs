using ImageMagick;

Console.WriteLine("M0: 在 NativeAOT 运行时探测 Magick.NET ...");

// 用 Magick.NET 创建并解码一张图，验证原生库在 AOT 裁剪后仍能加载
using var img = new MagickImage(new MagickColor(255, 215, 0), 640, 480);
img.Format = MagickFormat.Png;
var data = img.ToByteArray(MagickFormat.Png);

Console.WriteLine($"OK: 解码 {img.Width}x{img.Height}, PNG 字节数={data.Length}");
Console.WriteLine("M0 验证通过：Magick.NET 在 NativeAOT 下可运行时加载。");
