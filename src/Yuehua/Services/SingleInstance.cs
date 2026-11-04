using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Yuehua.Services;

/// <summary>
/// 单实例 + 「打开这些文件」的转发通道。
///
/// 为什么要它：把图片拖到桌面快捷方式上时，Explorer 会启动
/// <c>Yuehua.exe "图1.png"</c>。如果每次拖放都开一个新窗口，用户很快会有一堆窗口、
/// 还各自占着一份解码缓存。这里做两件事：
/// <list type="number">
///   <item>用命名互斥体判断是否已有实例，已有则不再抢。</item>
///   <item>首个实例轮询一个临时目录；后来的实例把路径写成一个小文件就退出，
///         由已有窗口就地显示（必要时还原 / 激活）。</item>
/// </list>
/// 用「目录 + 小文件」而不是命名管道：管道在受限环境（沙箱 / 低权限令牌）下
/// 经常连不上，而写临时文件任何环境都成立，代价只是最多 200ms 的延迟。
/// 任何一步失败都静默降级成「多开」，不影响看图这个主功能。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\Yuehua.SingleInstance";
    private const string RequestPrefix = "open-";
    private const string RequestSuffix = ".txt";

    private readonly string _dir;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private Thread? _thread;
    private volatile bool _stopping;
    private readonly object _gate = new();
    private readonly List<string> _pending = new();

    public SingleInstanceService()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Yuehua", "open");
    }

    /// <summary>收到外来路径时的回调（在后台线程触发，调用方需自行切到 UI 线程）。</summary>
    public Action<IReadOnlyList<string>>? Handler { get; set; }

    /// <summary>
    /// 尝试成为「首个实例」。返回 true 表示本进程负责监听转发请求；
    /// false 表示已有实例在跑（此时可调用 <see cref="SendPaths"/> 把参数转过去）。
    /// </summary>
    public bool TryBecomePrimary()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _ownsMutex = createdNew;
        }
        catch
        {
            // 互斥体不可用（权限 / 平台差异）：退化成多开，绝不因此起不来
            _ownsMutex = false;
            return false;
        }

        if (!_ownsMutex) return false;

        DeleteStaleRequests();
        StartListener();
        return true;
    }

    /// <summary>
    /// 把路径转交给已在运行的实例。只有确认对方取走了才返回 true，
    /// 否则返回 false 让调用方自己开窗口（宁可多开，也不能吞掉用户拖进来的图）。
    /// </summary>
    public bool SendPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        var file = WriteRequest(paths);
        if (file == null) return false;

        // 等对方把文件取走：取走 = 已接手，本进程可以安静退出
        var deadline = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < deadline)
        {
            try { if (!File.Exists(file)) return true; }
            catch { return true; }
            Thread.Sleep(40);
        }

        try { File.Delete(file); } catch { /* ignore */ }
        return false;
    }

    /// <summary>取出「窗口还没建好时」攒下的路径（在主窗口就绪后调用一次）。</summary>
    public List<string> DrainPending()
    {
        lock (_gate)
        {
            var copy = new List<string>(_pending);
            _pending.Clear();
            return copy;
        }
    }

    private string? WriteRequest(IReadOnlyList<string> paths)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var name = RequestPrefix + DateTime.UtcNow.Ticks + "-" + Environment.ProcessId + "-" +
                       Guid.NewGuid().ToString("N")[..6] + RequestSuffix;
            var target = Path.Combine(_dir, name);
            var temp = target + ".tmp";

            var sb = new StringBuilder();
            foreach (var p in paths) sb.Append(p).Append('\n');
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));

            // 先写临时文件再改名：监听方永远读不到写了一半的内容
            File.Move(temp, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private void StartListener()
    {
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Yuehua.OpenRequests",
        };
        _thread.Start();
    }

    private void ListenLoop()
    {
        while (!_stopping)
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    foreach (var file in Directory.EnumerateFiles(_dir, RequestPrefix + "*" + RequestSuffix))
                    {
                        if (_stopping) return;
                        TakeRequest(file);
                    }
                }
            }
            catch
            {
                // 目录被清理 / 文件被占用：下一轮再看
            }

            for (var i = 0; i < 5 && !_stopping; i++) Thread.Sleep(40);   // ≈200ms 一轮
        }
    }

    private void TakeRequest(string file)
    {
        List<string> batch;
        try
        {
            batch = new List<string>();
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var p = line.Trim();
                if (p.Length > 0) batch.Add(p);
            }

            File.Delete(file);      // 先删再交：避免自己或下一轮重复打开
        }
        catch
        {
            return;
        }

        if (batch.Count > 0) Deliver(batch);
    }

    private void Deliver(List<string> paths)
    {
        var handler = Handler;
        if (handler is null)
        {
            // 主窗口还没建好：先记下来，App 初始化完再取
            lock (_gate) _pending.AddRange(paths);
            return;
        }
        try { handler(paths); }
        catch { /* 打开失败不应拖垮监听线程 */ }
    }

    /// <summary>清掉上次异常退出（或无人接手）留下的老请求，避免下次启动凭空打开一批旧图。</summary>
    private void DeleteStaleRequests()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _thread?.Join(300); } catch { /* ignore */ }

        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        }
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }
}
