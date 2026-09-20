using System.Runtime.InteropServices;

namespace DragSource;

/// <summary>
/// 用真实的 OLE 拖放把一个或多个文件拖到屏幕上的指定坐标。
///
/// 存在的理由见 dragsource.csproj。这里只记两个踩过的坑：
///
///   1. 源窗口必须在**屏幕内**。DoDragDrop 的前提是左键真的按在源窗口上，
///      而 SetCursorPos 到屏幕外会被系统钳制回屏幕边缘 —— 按下动作就落到了别处，
///      DoDragDrop 会卡在模态循环里永远不返回（实测挂了十几分钟）。
///   2. 必须跑真正的消息循环（Application.Run）。只 Show 一下再 DoEvents
///      是不够的：拖放期间源窗口要处理鼠标消息，没有消息泵就死在那儿。
///
/// 另外挂了个看门狗：真挂住也会在超时后退出并给出非 0 退出码，不会让调用方干等。
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point p);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [STAThread]
    private static int Main(string[] args)
    {
        // 中文 Windows 的控制台默认是 GBK，调用方按 UTF-8 读就全是乱码
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 设不了就算了 */ }

        var files = new List<string>();
        int targetX = int.MinValue, targetY = int.MinValue;
        int sourceX = 40, sourceY = 40;
        int holdMs = 800, steps = 24, timeoutMs = 20000;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file":
                    if (++i < args.Length) files.Add(args[i]);
                    break;
                case "--to":
                    if (i + 2 < args.Length)
                    {
                        int.TryParse(args[i + 1], out targetX);
                        int.TryParse(args[i + 2], out targetY);
                        i += 2;
                    }
                    break;
                case "--from":
                    if (i + 2 < args.Length)
                    {
                        int.TryParse(args[i + 1], out sourceX);
                        int.TryParse(args[i + 2], out sourceY);
                        i += 2;
                    }
                    break;
                case "--hold":
                    if (++i < args.Length) int.TryParse(args[i], out holdMs);
                    break;
                case "--timeout":
                    if (++i < args.Length) int.TryParse(args[i], out timeoutMs);
                    break;
            }
        }

        if (files.Count == 0 || targetX == int.MinValue)
        {
            Console.Error.WriteLine("用法：dragsource.exe --to <x> <y> --file <路径> [--file <路径>...] [--from x y] [--hold 毫秒]");
            return 2;
        }

        foreach (var f in files)
        {
            if (!File.Exists(f))
            {
                Console.Error.WriteLine($"文件不存在：{f}");
                return 2;
            }
        }

        Console.WriteLine($"拖 {files.Count} 个文件：源窗口 ({sourceX}, {sourceY}) → 目标点 ({targetX}, {targetY})");

        var target = new Point(targetX, targetY);

        var source = new Form
        {
            Text = "dragsource-holder",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(sourceX, sourceY),
            Size = new Size(200, 120),
            ShowInTaskbar = false,
        };

        // 看门狗：真卡住也要退出来，别让调用方等到天荒地老
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(timeoutMs);
            Console.WriteLine($"watchdog：{timeoutMs} ms 内没走完，强退");
            Console.Out.Flush();
            Environment.Exit(3);
        }) { IsBackground = true };
        watchdog.Start();

        // 拖放必须在**窗口自己收到左键按下消息**的上下文里发起（MouseDown 事件处理内），
        // 不能"先合成一次按下、再直接调 DoDragDrop" —— 那样 OLE 的模态循环会卡住不返回
        // （实测挂到看门狗超时）。所以按下交给 mouse_event 合成，拖放从这里发起。
        source.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;

            Console.WriteLine("源窗口收到左键按下 → 发起 DoDragDrop");
            Console.Out.Flush();

            StartDriver(target, holdMs, steps);

            var data = new DataObject(DataFormats.FileDrop, files.ToArray());
            var effect = source.DoDragDrop(data, DragDropEffects.Copy);

            Console.WriteLine($"effect={effect}");
            Console.Out.Flush();

            source.Close();
        };

        source.Shown += (_, _) =>
        {
            source.Activate();
            Application.DoEvents();
            Thread.Sleep(300);

            // 用窗口自己算出来的屏幕坐标，不要用 Left/Top + 尺寸 —— 那套是 DIP，
            // 在带 DPI 缩放的屏幕上和物理像素对不上，光标会落到窗口外面；
            // 而 DoDragDrop 的前提正是"左键真的按在源窗口上"，落外面就卡死。
            var center = source.PointToScreen(new Point(source.Width / 2, source.Height / 2));
            Console.WriteLine($"源窗口 Bounds={source.Bounds}｜中心（屏幕坐标）=({center.X},{center.Y})");

            SetCursorPos(center.X, center.Y);
            Application.DoEvents();
            Thread.Sleep(250);

            GetCursorPos(out var actual);
            Console.WriteLine($"期望按下点 {center.X},{center.Y}｜实际光标 {actual.X},{actual.Y}");

            // 对不上就别硬来：DoDragDrop 会在模态循环里一直等，白等 20 秒才被看门狗砍掉
            if (actual != center)
            {
                Console.WriteLine("按下点不在源窗口上 → 放弃这次拖放（不然 DoDragDrop 会卡住）");
                Console.Out.Flush();
                Environment.Exit(4);
            }

            Console.Out.Flush();
            mouse_event(MouseEventLeftDown, 0, 0, 0, 0);
        };

        Application.Run(source);
        return 0;
    }

    /// <summary>
    /// 拖动过程：分步把光标移到目标，停一会儿让目标窗口收 DragOver，
    /// 然后松开左键结束拖放。放在独立线程里 —— DoDragDrop 会一直占着主线程。
    /// </summary>
    private static void StartDriver(Point target, int holdMs, int steps)
    {
        var driver = new Thread(() =>
        {
            GetCursorPos(out var start);
            Thread.Sleep(250);

            int fromX = start.X, fromY = start.Y;
            int stepX = target.X - fromX, stepY = target.Y - fromY;

            for (int i = 1; i <= steps; i++)
            {
                SetCursorPos(fromX + (int)Math.Round(stepX * (double)i / steps),
                             fromY + (int)Math.Round(stepY * (double)i / steps));
                Thread.Sleep(25);
            }

            Thread.Sleep(holdMs);
            mouse_event(MouseEventLeftUp, 0, 0, 0, 0);
        })
        { IsBackground = true };
        driver.Start();
    }
}
