using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Yuehua.Views;

namespace Yuehua;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // 外部来的「打开这些图片」请求：先接上转发通道（避免启动期丢消息），
            // 再把启动前攒下的（别的进程已转发但窗口还没建好）和本次命令行参数交给窗口。
            var single = AppStartup.Single;
            if (single != null)
            {
                single.Handler = paths => Dispatcher.UIThread.Post(() => window.OpenPathsFromOutside(paths));

                var pending = single.DrainPending();
                if (pending.Count > 0)
                    Dispatcher.UIThread.Post(() => window.OpenPathsFromOutside(pending), DispatcherPriority.Background);
            }

            var initial = AppStartup.InitialPaths;
            if (initial.Count > 0)
                Dispatcher.UIThread.Post(() => window.OpenPathsFromOutside(initial), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
