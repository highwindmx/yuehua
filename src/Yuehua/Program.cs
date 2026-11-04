using System;
using System.Collections.Generic;
using Avalonia;
using Yuehua.Services;

namespace Yuehua;

internal sealed class Program
{
    // 原生 AOT 入口需要 STA 线程
    [STAThread]
    public static void Main(string[] args)
    {
        var (paths, forceNew) = SplitArgs(args);

        // 单实例 + 转发：已有窗口就把它叫到前台打开这批图片，不再开第二个窗口。
        // 加 --new-instance 可以强制多开（用来对比、并排看图）。
        using var single = new SingleInstanceService();
        AppStartup.Single = single;

        var primary = single.TryBecomePrimary();
        if (!primary && !forceNew && paths.Count > 0 && single.SendPaths(paths))
            return;                       // 已交给运行中的窗口，本进程直接退出

        AppStartup.InitialPaths = paths;
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// 拆分命令行：去掉引号、跳过开关，剩下的按文件 / 文件夹处理。
    /// 拖动图片到快捷方式时，Explorer 传进来的就是裸路径（可能带空格和引号）。
    /// </summary>
    private static (List<string> Paths, bool ForceNew) SplitArgs(string[] args)
    {
        var paths = new List<string>();
        var forceNew = false;
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var a = raw.Trim().Trim('"');
            if (a.Length == 0) continue;

            if (a.Equals("--new-instance", StringComparison.OrdinalIgnoreCase))
            {
                forceNew = true;
                continue;
            }
            if (a[0] == '-' || a.Equals("/new", StringComparison.OrdinalIgnoreCase))
                continue;                 // 其它开关：本程序不认，也不当成文件

            paths.Add(a);
        }
        return (paths, forceNew);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>启动期在两个类之间传递「本次要打开的路径 + 单实例通道」。</summary>
internal static class AppStartup
{
    public static SingleInstanceService? Single { get; set; }
    public static IReadOnlyList<string> InitialPaths { get; set; } = Array.Empty<string>();
}
