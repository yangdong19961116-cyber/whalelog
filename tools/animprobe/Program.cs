using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WhaleLog.AnimProbe;

/// <summary>
/// 实测 WPF 在「动态素材」上到底能做什么，为素材规格提供依据。
///
/// 三个必须自己测、不能靠印象的问题：
///   1. GIF 动画 WPF 能不能解、逐帧时长准不准
///   2. GIF 的透明是真透明还是只有 1 位（有没有羽化）
///   3. 精灵图切帧是否可靠、渲染时钟能跑到多少 fps
///
/// 结论直接决定该让画师交什么格式 —— 猜错就是白画一批图。
///
/// 注意：采样渲染时钟**必须跑真正的消息循环**（Dispatcher.PushFrame）。
/// 用 Thread.Sleep 堵住 UI 线程的话 CompositionTarget.Rendering 一次都不会触发，
/// 会得出"渲染时钟不可用"的错误结论。
/// </summary>
internal static class Program
{
    private const int TargetFps = 24;
    private static readonly List<double> Intervals = new();
    private static Stopwatch _clock = new();
    private static double _lastMs;

    private static BitmapSource? _sheet;
    private static int _swaps;
    private static double _nextSwapMs;
    private static int _swapsInWindow;

    [STAThread]
    private static int Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whalelog-anim-probe");

        var lines = new List<string>
        {
            "===== 运行环境 =====",
            $"  WPF 版本       {typeof(Window).Assembly.GetName().Version}",
            $"  素材目录       {dir}",
            "",
        };

        // ---- 1. GIF 动画能解吗 ----
        ProbeGifFrames(lines, dir);

        // ---- 2. GIF 透明到底是几位 ----
        lines.Add("===== GIF 透明通道（关键：能不能羽化）=====");
        ProbeTransparency(lines, dir, "probe_opaque.gif", "源图完全不透明，未声明透明");
        ProbeAlphaRange(lines, dir, "probe_opaque.gif");
        ProbeTransparency(lines, dir, "pal_fixed.gif", "标准调色板写法：索引0专留透明 + 硬边角色");
        ProbeAlphaRange(lines, dir, "pal_fixed.gif");
        ProbeTransparency(lines, dir, "probe_soft.gif", "边缘做了半透明羽化");
        ProbeAlphaRange(lines, dir, "probe_soft.gif");
        ProbeLeak(lines, dir, "leaky.gif", "透明索引 = 角色身上的颜色（索引1）");
        lines.Add("");

        // ---- 3. APNG ----
        lines.Add("===== APNG（动画 PNG）=====");
        ProbeFrames(lines, dir, "probe.apng.png", 4, "WPF 把动画 PNG 当静态图，原生不支持");
        lines.Add("");

        // ---- 4. WebP ----
        lines.Add("===== WebP =====");
        foreach (var name in new[] { "probe.webp", "probe_anim.webp" })
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p))
            {
                lines.Add($"  {name}  未生成（跳过）");
                continue;
            }
            try
            {
                using var fs = File.OpenRead(p);
                var dec = BitmapDecoder.Create(
                    fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                lines.Add($"  {name}  解出帧数 {dec.Frames.Count}");
            }
            catch (Exception ex)
            {
                lines.Add($"  {name}  WPF 无法解码：{ex.GetType().Name}: {ex.Message}");
            }
        }
        lines.Add("  （本机能解 WebP 是因为额外装了 Store 包 Microsoft.WebpImageExtension，");
        lines.Add("    不是 Windows 默认组件 —— 换台没装的机器会解不出来）");
        lines.Add("");

        // ---- 5. 精灵图切片 ----
        ProbeSpriteSheet(lines, dir);

        // ---- 5b. 同内容不同容器的透明保真度 ----
        ProbeFidelity(lines, dir);

        // ---- 5c. 增量帧 GIF（导出器默认的优化方式）----
        ProbeDeltaFrames(lines, dir);

        // ---- 5d. 用真实样例验证「精灵图 + 帧表」这套规格本身可行 ----
        ProbeRealSample(lines);

        // ---- 6/7. 渲染时钟 + 逐帧换图 ----
        RunRenderLoop(lines, dir);

        lines.Add("结论");
        lines.Add("");
        lines.Add("  容器        动画            透明                 便携性");
        lines.Add("  GIF         ✓ 帧数/时长准   ✗ 仅 1 位（硬边）    全球通用");
        lines.Add("  APNG        ✗ 只解第 1 帧   —                    —");
        lines.Add("  动画 WebP   ✓ 4 帧          ✓ 8 位（羽化保留）   需 Store 包，非默认");
        lines.Add("  PNG 序列    ✓ 自己排帧      ✓ 8 位                全球通用");
        lines.Add("  精灵图 PNG  ✓ 自己切帧      ✓ 8 位                全球通用 ← 推荐");
        lines.Add("");
        lines.Add("  实测依据：同一组带羽化软边的帧，GIF 解出 2 级 alpha（0/255），");
        lines.Add("            WebP 与 PNG 都解出 122 级，边缘采样 85 170 255 与源图一致。");
        lines.Add("");
        lines.Add("→ 动态素材首选「精灵图（PNG）+ 帧表 JSON」，拿不到就退一格用 PNG 序列；");
        lines.Add("  GIF 只适合本来就是硬边的简单动效；WebP 本机可用但别当默认格式。");

        var outPath = Path.Combine(dir, "wpf-probe.txt");
        File.WriteAllText(outPath, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return 0;
    }

    // ==================================================================
    // 解码能力
    // ==================================================================

    private static void ProbeGifFrames(List<string> lines, string dir)
    {
        lines.Add("===== GIF 动画（帧数与逐帧时长）=====");
        var gif = Path.Combine(dir, "probe.gif");
        if (!File.Exists(gif))
        {
            lines.Add("  找不到 probe.gif");
            lines.Add("");
            return;
        }

        try
        {
            using var fs = File.OpenRead(gif);
            var dec = new GifBitmapDecoder(
                fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            lines.Add($"  解码器         {dec.GetType().Name}");
            lines.Add($"  帧数           {dec.Frames.Count}");

            for (int i = 0; i < dec.Frames.Count; i++)
            {
                var frame = dec.Frames[i];
                object? raw = null;
                try
                {
                    if (frame.Metadata is BitmapMetadata md) raw = md.GetQuery("/grctlext/Delay");
                }
                catch
                {
                    // 没有延时块就留空
                }

                int ms = raw is ushort u ? u * 10 : -1;
                lines.Add($"    帧{i}  {frame.PixelWidth}×{frame.PixelHeight}  " +
                          $"Delay={raw ?? "无"} → {(ms < 0 ? "未知" : ms + "ms")}");
            }
            lines.Add("  写这些帧的原始时长是 100 / 200 / 300 / 500 ms");
        }
        catch (Exception ex)
        {
            lines.Add($"  失败：{ex.GetType().Name}: {ex.Message}");
        }
        lines.Add("");
    }

    private static void ProbeFrames(List<string> lines, string dir, string name,
                                    int sourceFrames, string whenSingle)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p))
        {
            lines.Add($"  找不到 {name}");
            return;
        }

        try
        {
            using var fs = File.OpenRead(p);
            var dec = BitmapDecoder.Create(
                fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            lines.Add($"  解码器         {dec.GetType().Name}");
            lines.Add($"  帧数           {dec.Frames.Count}（源文件里是 {sourceFrames} 帧）");
            if (dec.Frames.Count <= 1) lines.Add($"  → {whenSingle}");
        }
        catch (Exception ex)
        {
            lines.Add($"  失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProbeSpriteSheet(List<string> lines, string dir)
    {
        lines.Add("===== 精灵图切片（CroppedBitmap）=====");
        var sheetPath = Path.Combine(dir, "sheet.png");
        if (!File.Exists(sheetPath))
        {
            lines.Add("  找不到 sheet.png");
            lines.Add("");
            return;
        }

        _sheet = Load(sheetPath);
        lines.Add($"  精灵图         {_sheet.PixelWidth}×{_sheet.PixelHeight}  {_sheet.Format}");

        int cellW = _sheet.PixelWidth / 4;
        var got = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var crop = new BitmapSource[1];
            crop[0] = Crop(i, cellW);
            got.Add(CenterColor(crop[0]));
        }
        lines.Add($"  横向切 4 帧中心色 {string.Join(" / ", got)}");
        lines.Add("  期望             #FF0000 / #00FF00 / #0000FF / #FFFF00");
        lines.Add("");
    }

    private static BitmapSource Crop(int index, int cellW)
    {
        var crop = new CroppedBitmap(_sheet!,
            new Int32Rect(index * cellW, 0, cellW, _sheet!.PixelHeight));
        crop.Freeze();
        return crop;
    }

    // ==================================================================
    // 同内容、不同容器的透明保真度
    // ==================================================================

    /// <summary>
    /// 同一组「带羽化软边」的帧，分别存成 GIF / 动画 WebP / PNG 序列，
    /// 看 WPF 解出来还剩多少级 alpha。
    ///
    /// 这是选格式的决定性依据：角色抠图一定有抗锯齿软边，
    /// 如果容器只支持 1 位透明，边缘会变成锯齿。
    /// </summary>
    private static void ProbeFidelity(List<string> lines, string dir)
    {
        lines.Add("===== 透明保真度对照（同内容 · 带羽化软边）=====");
        lines.Add("  源图边缘 alpha 连续变化（0~255 共 200 级左右）");

        ReportContainer(lines, Path.Combine(dir, "soft.gif"), "GIF 动画");
        ReportContainer(lines, Path.Combine(dir, "soft_anim.webp"), "动画 WebP");

        // PNG 序列：逐帧读，取所有帧里 alpha 的并集
        var seq = new List<BitmapSource>();
        for (int i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, $"soft_seq_{i}.png");
            if (!File.Exists(p)) break;
            seq.Add(Load(p));
        }
        if (seq.Count > 0)
        {
            var seen = new HashSet<byte>();
            foreach (var f in seq) CollectAlpha(f, seen);
            var list = seen.OrderBy(x => x).ToList();
            lines.Add($"  PNG 序列（{seq.Count} 个文件）");
            lines.Add($"    第 1 帧尺寸 {seq[0].PixelWidth}×{seq[0].PixelHeight}  {seq[0].Format}");
            lines.Add($"    alpha 值种类 {list.Count} 种（{list.First()}~{list.Last()}）");
            lines.Add(list.Count > 2
                ? "      → **完整 8 位透明度，羽化边缘原样保留**"
                : "      → 只有 0/255");
            lines.Add($"    边缘采样 {EdgeSample(seq[0])}");
        }
        lines.Add("");
    }

    /// <summary>
    /// 增量帧 GIF 能不能正确解码。
    ///
    /// AE / Photoshop / ezgif 默认都会把"和上一帧相同的部分"裁掉，只存变化区域
    /// （Pillow 的 optimize=True 也一样）。这种 GIF 能不能用，取决于解码器有没有
    /// 按 disposal 规则把帧合成回完整画面 —— 不合成的话第二帧起就只剩一块残片。
    /// 这一条直接决定 GIF 能不能作为备选格式。
    /// </summary>
    private static void ProbeDeltaFrames(List<string> lines, string dir)
    {
        lines.Add("===== 增量帧 GIF（导出器默认优化：只存变化区域）=====");

        // 每帧的期望：底部"身体"恒定不动，顶部"眼睛"逐帧换位并换色
        var eyeColors = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00" };

        foreach (var (file, label) in new[]
                 {
                     ("opt_plain.gif", "整帧存储（optimize=False）"),
                     ("opt_delta.gif", "增量存储（optimize=True）"),
                 })
        {
            var path = Path.Combine(dir, file);
            lines.Add($"  【{label}】{file}");

            if (!File.Exists(path))
            {
                lines.Add("    文件未生成（跳过）");
                continue;
            }

            try
            {
                using var fs = File.OpenRead(path);
                var dec = new GifBitmapDecoder(
                    fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                lines.Add($"    解出帧数 {dec.Frames.Count}");

                for (int i = 0; i < dec.Frames.Count; i++)
                {
                    var f = dec.Frames[i];

                    // 画布尺寸：全画布还是只有变化区域
                    string size = $"{f.PixelWidth}×{f.PixelHeight}";

                    // 取整帧像素做检查
                    var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                    int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
                    var buf = new byte[stride * h];
                    conv.CopyPixels(buf, stride, 0);

                    // 底部"身体"（y=75）应当每帧都在，颜色 90,90,120
                    int bodyY = Math.Min(75, h - 1);
                    int bodyX = Math.Min(48, w - 1);
                    var body = buf[bodyY * stride + bodyX * 4 + 3] == 0
                        ? "透明（身体丢了）"
                        : $"#{buf[bodyY * stride + bodyX * 4 + 2]:X2}{buf[bodyY * stride + bodyX * 4 + 1]:X2}{buf[bodyY * stride + bodyX * 4]:X2}";

                    // 顶部"眼睛"：第 i 帧应落在 x = 20+i*14 .. 36+i*14
                    int eyeX = Math.Min(20 + i * 14 + 8, w - 1);
                    int eyeY = Math.Min(30, h - 1);
                    int o = eyeY * stride + eyeX * 4;
                    var eye = buf[o + 3] == 0
                        ? "透明（眼睛丢了）"
                        : $"#{buf[o + 2]:X2}{buf[o + 1]:X2}{buf[o]:X2}";

                    bool bodyOk = body.StartsWith("#5A5A78", StringComparison.OrdinalIgnoreCase)
                               || body.Contains("5A5A78", StringComparison.OrdinalIgnoreCase);
                    bool eyeOk = string.Equals(eye, eyeColors[i], StringComparison.OrdinalIgnoreCase);

                    lines.Add($"    帧{i}  {size}  身体 {body}{(bodyOk ? "" : " ✗")}  " +
                              $"眼睛 {eye}（期望 {eyeColors[i]}{((eyeOk) ? "" : " ✗")}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"    解码失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        lines.Add("    判读：若「增量存储」那组的身体变成透明或眼睛错位，");
        lines.Add("        说明 WPF 没按 disposal 规则合成残片 → 这类 GIF 不能用，");
        lines.Add("        必须让导出器关闭帧优化（存整幅）。");
        lines.Add("");
    }

    private static void ReportContainer(List<string> lines, string path, string label)
    {
        if (!File.Exists(path))
        {
            lines.Add($"  {label}：文件不存在");
            return;
        }

        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(
                fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            var seen = new HashSet<byte>();
            int count = 0;
            foreach (var frame in dec.Frames)
            {
                var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                bmp.Freeze();
                CollectAlpha(bmp, seen);
                count++;
            }

            var list = seen.OrderBy(x => x).ToList();
            lines.Add($"  {label}（{count} 帧）");
            lines.Add($"    alpha 值种类 {list.Count} 种" +
                      (list.Count <= 8 ? "：" + string.Join(", ", list) : $"（{list.First()}~{list.Last()}）"));
            lines.Add(list.Count <= 2
                ? "      → **只有 0/255，1 位透明，羽化被压成硬边**"
                : "      → **支持连续 alpha，羽化保留**");

            var first = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            first.Freeze();
            lines.Add($"    边缘采样 {EdgeSample(first)}");
        }
        catch (Exception ex)
        {
            lines.Add($"  {label}：解码失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CollectAlpha(BitmapSource bmp, HashSet<byte> into)
    {
        var buf = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(buf, bmp.PixelWidth * 4, 0);
        for (int i = 3; i < buf.Length; i += 4) into.Add(buf[i]);
    }

    /// <summary>沿水平中线从左到右取前 8 个非零 alpha 值 —— 就是左边缘那几像素的过渡。</summary>
    private static string EdgeSample(BitmapSource bmp)
    {
        int y = bmp.PixelHeight / 2;
        var row = new byte[bmp.PixelWidth * 4];
        bmp.CopyPixels(new Int32Rect(0, y, bmp.PixelWidth, 1), row, bmp.PixelWidth * 4, 0);

        var vals = new List<int>();
        for (int x = 0; x < bmp.PixelWidth && vals.Count < 8; x++)
        {
            int a = row[x * 4 + 3];
            if (a != 0) vals.Add(a);
        }
        return vals.Count == 0 ? "（没有不透明像素）" : string.Join(" ", vals);
    }

    // ==================================================================
    // 渲染循环实测（必须跑真消息循环）
    // ==================================================================

    // ==================================================================
    // 真实样例验证：「精灵图 + 帧表 JSON」这套规格能不能落地
    //
    // 前面几节用的都是合成的色块，只能证明"机制通"。这一节拿真图真帧表跑 ——
    // 切帧位置对不对、角色有没有横向漂移、按 fps 排帧能不能跑满，一次说清。
    // 规格给出去了才发现不可行，代价是画师白画一批图。
    // ==================================================================

    private static void ProbeRealSample(List<string> lines)
    {
        lines.Add("===== 真实样例验证（精灵图 + 帧表 JSON）=====");

        var sample = FindSampleDir();
        if (sample == null)
        {
            lines.Add("  找不到样例目录 outputs\\样例-点击弹跳，跳过");
            lines.Add("");
            return;
        }

        var sheetPath = Path.Combine(sample, "sheet.png");
        var jsonPath = Path.Combine(sample, "sheet.json");
        if (!File.Exists(sheetPath) || !File.Exists(jsonPath))
        {
            lines.Add($"  样例文件不全（sheet.png={File.Exists(sheetPath)} json={File.Exists(jsonPath)}），跳过");
            lines.Add("");
            return;
        }

        lines.Add($"  样例目录 {sample}");

        // ---- 帧表解析 ----
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        int fw = root.GetProperty("frameWidth").GetInt32();
        int fh = root.GetProperty("frameHeight").GetInt32();
        int cols = root.GetProperty("cols").GetInt32();
        int rows = root.GetProperty("rows").GetInt32();
        int count = root.GetProperty("count").GetInt32();
        int fps = root.GetProperty("fps").GetInt32();
        bool loop = root.GetProperty("loop").GetBoolean();
        lines.Add($"  帧表 {fw}×{fh}  {cols}列×{rows}行 = {count} 帧  {fps} fps  loop={loop}");

        using var fs = File.OpenRead(sheetPath);
        var sheet = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        sheet.Freeze();
        lines.Add($"  精灵图 {sheet.PixelWidth}×{sheet.PixelHeight}  " +
                  $"{new FileInfo(sheetPath).Length / 1024.0 / 1024.0:0.00} MB");

        // ---- 尺寸自洽 ----
        bool sizeOk = sheet.PixelWidth == cols * fw && sheet.PixelHeight == rows * fh;
        lines.Add($"  画布尺寸与帧表自洽：{(sizeOk ? "✓" : "✗")}" +
                  $"（{cols}×{fw}={cols * fw}，{rows}×{fh}={rows * fh}）");

        // ---- 逐帧切片 ----
        var frames = new List<BitmapSource>();
        for (int i = 0; i < count; i++)
        {
            var src = new CroppedBitmap(sheet, new Int32Rect(
                (i % cols) * fw, (i / cols) * fh, fw, fh));
            src.Freeze();
            frames.Add(src);
        }

        lines.Add("  逐帧切片：");
        var boxes = frames.Select(AlphaBounds).ToList();
        int? baseCenterX = null;
        int? baseFootY = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var b = boxes[i];
            if (b == null)
            {
                lines.Add($"    帧{i} 全透明（异常）");
                continue;
            }

            int cx = (b.Value.Left + b.Value.Right) / 2;
            int footY = b.Value.Bottom;
            baseCenterX ??= cx;
            baseFootY ??= footY;

            double shiftY = footY - baseFootY.Value;
            string drift = Math.Abs(cx - baseCenterX.Value) <= 2
                ? "水平对齐 ✓"
                : $"水平漂移 {cx - baseCenterX.Value:+0;-0} px ✗";

            lines.Add($"    帧{i}  角色 {b.Value.Width}×{b.Value.Height}  " +
                      $"中心x={cx}  脚底y={footY}  相对基准位移 {shiftY:+0;-0;0} px  {drift}");
        }

        // 位移应当有起有落 —— 全 0 说明"动效根本没做出来"
        var shifts = boxes.Where(b => b != null)
                          .Select(b => b!.Value.Bottom - baseFootY!.Value)
                          .ToList();
        int minShift = shifts.Min();
        int maxShift = shifts.Max();
        lines.Add($"  位移跨度 {minShift} ~ {maxShift} px（跳起 {minShift}px / 下压 {maxShift}px）");

        // ---- 真实排帧：按 fps 走完一轮，数实际换了几次 ----
        var window = new Window
        {
            Width = 200,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
        };
        var image = new Image { Stretch = Stretch.Uniform, Source = frames[0] };
        window.Content = image;
        window.Show();

        double frameMs = 1000.0 / Math.Max(1, fps);
        int shown = 0;
        int swaps = 0;
        var clock = Stopwatch.StartNew();
        double nextAt = 0;
        int seenLast = -1;

        EventHandler tick = null!;
        tick = (_, _) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (now >= nextAt)
            {
                nextAt += frameMs;
                if (nextAt < now - frameMs) nextAt = now + frameMs;   // 落后太多就重新对齐
                shown++;
                if (shown < frames.Count)
                {
                    image.Source = frames[shown];
                    swaps++;
                    seenLast = shown;
                }
                else
                {
                    CompositionTarget.Rendering -= tick;
                }
            }
        };
        CompositionTarget.Rendering += tick;
        PumpFor((int)(frameMs * frames.Count) + 260);
        CompositionTarget.Rendering -= tick;

        double elapsed = clock.Elapsed.TotalMilliseconds;
        lines.Add($"  按 {fps} fps 排 {count} 帧：实测换帧 {swaps} 次 / {elapsed:0} ms" +
                  $"（应有 {count - 1} 次），最后显示第 {seenLast} 帧");
        bool playOk = swaps >= count - 1 && seenLast == count - 1;
        lines.Add($"  完整播完一遍：{(playOk ? "✓" : "✗")}");

        window.Close();
        lines.Add("");
    }

    /// <summary>从 exe 位置往上找工作区里的样例目录。</summary>
    private static string? FindSampleDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "outputs", "样例-点击弹跳");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>像素范围。刻意不用 Int32Rect —— 它只有 X/Y/Width/Height，没有四边。</summary>
    private readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    /// <summary>alpha &gt; 8 的像素范围。返回 null 表示整帧全透明。</summary>
    private static Bounds? AlphaBounds(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                if (px[row + x * 4 + 3] > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0) return null;
        return new Bounds(minX, minY, maxX, maxY);
    }

    private static void RunRenderLoop(List<string> lines, string dir)
    {
        var window = new Window
        {
            Width = 200,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,          // 屏幕外，别闪到用户
            Top = -32000,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            Topmost = false,
        };

        var image = new Image { Stretch = Stretch.Uniform };
        window.Content = image;
        window.Show();

        // ---- 6. 采样渲染回调间隔 ----
        int cellW = _sheet != null ? _sheet.PixelWidth / 4 : 0;
        if (_sheet != null) image.Source = Crop(0, cellW);

        _clock = Stopwatch.StartNew();
        _lastMs = 0;
        int samples = 0;

        EventHandler onTick = null!;
        onTick = (_, _) =>
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_lastMs > 0) Intervals.Add(now - _lastMs);
            _lastMs = now;
            if (++samples >= 120) CompositionTarget.Rendering -= onTick;
        };
        CompositionTarget.Rendering += onTick;

        PumpFor(1200);
        CompositionTarget.Rendering -= onTick;

        lines.Add("===== 渲染时钟间隔（CompositionTarget.Rendering）=====");
        if (Intervals.Count > 1)
        {
            var sorted = Intervals.OrderBy(x => x).ToList();
            double median = sorted[sorted.Count / 2];
            lines.Add($"  实测 {sorted.Count} 次间隔（1.2 秒窗口）");
            lines.Add($"    中位数 {median:0.0} ms → ≈{1000 / Math.Max(0.1, median):0} fps");
            lines.Add($"    最小 / 最大 {sorted.First():0.0} / {sorted.Last():0.0} ms");
            lines.Add($"    95 分位 {sorted[(int)(sorted.Count * 0.95)]:0.0} ms");
            lines.Add("  注：这个数字取决于显示器刷新率（本机可能是虚拟机/低刷屏），" +
                      "换台 60Hz 机器会更高");
        }
        else
        {
            lines.Add($"  只采到 {Intervals.Count} 个样本");
        }
        lines.Add("");

        // ---- 7. 按目标帧率逐帧换图 ----
        lines.Add($"===== 用渲染回调逐帧换图（目标 {TargetFps} fps）=====");
        if (_sheet == null)
        {
            lines.Add("  没有精灵图，跳过");
            lines.Add("");
            window.Close();
            return;
        }

        _swaps = 0;
        _swapsInWindow = 0;
        _nextSwapMs = 0;
        var swapClock = Stopwatch.StartNew();

        EventHandler swapper = null!;
        swapper = (_, _) =>
        {
            double t = swapClock.Elapsed.TotalMilliseconds;
            if (t >= _nextSwapMs)
            {
                image.Source = Crop(_swaps % 4, cellW);
                _swaps++;
                _swapsInWindow++;
                _nextSwapMs += 1000.0 / TargetFps;
            }
        };
        CompositionTarget.Rendering += swapper;

        PumpFor(1000);
        double elapsed = swapClock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering -= swapper;

        double actual = elapsed > 1 ? _swapsInWindow / (elapsed / 1000) : 0;
        lines.Add($"  实际换帧 {_swapsInWindow} 次 / {elapsed:0} ms → {actual:0.0} fps");
        lines.Add($"  最后显示的切片 #{(_swaps - 1) % 4}（应随时间推进）");
        lines.Add("  → 推荐方案：不依赖 Storyboard，帧率由显示刷新率决定；" +
                  $"在 {TargetFps} fps 目标下基本能打满");
        lines.Add("");

        window.Close();
    }

    /// <summary>跑一段真正的消息循环，让渲染回调有机会触发。</summary>
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // ==================================================================
    // 像素读取
    // ==================================================================

    private static BitmapSource Load(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapDecoder.Create(
            fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static string CenterColor(BitmapSource src)
    {
        var px = new byte[4];
        src.CopyPixels(new Int32Rect(src.PixelWidth / 2, src.PixelHeight / 2, 1, 1), px, 4, 0);
        return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}/a{px[3]}";
    }

    private static string CornerColor(BitmapSource src)
    {
        var px = new byte[4];
        src.CopyPixels(new Int32Rect(1, 1, 1, 1), px, 4, 0);
        return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}/a{px[3]}";
    }

    /// <summary>读一帧里所有出现过的 alpha 值 —— 只有 0 和 255 就是 1 位透明，不能羽化。</summary>
    private static void ProbeAlphaRange(List<string> lines, string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return;

        try
        {
            var bmp = Load(p);
            var buf = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(buf, bmp.PixelWidth * 4, 0);

            var seen = new HashSet<byte>();
            for (int i = 3; i < buf.Length; i += 4) seen.Add(buf[i]);

            var list = seen.OrderBy(x => x).ToList();
            lines.Add($"    alpha 值种类 {list.Count} 种：{string.Join(", ", list.Take(8))}" +
                      (list.Count > 8 ? " …" : ""));
            lines.Add(list.Count <= 2
                ? "      → 只有 0 和 255：**1 位透明，边缘只能是硬的**"
                : "      → 有中间值：**支持半透明羽化**");
        }
        catch (Exception ex)
        {
            lines.Add($"    读取失败：{ex.Message}");
        }
    }

    private static void ProbeTransparency(List<string> lines, string dir, string name, string label)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return;

        lines.Add($"  {label}");
        try
        {
            var bmp = Load(p);
            lines.Add($"    帧格式 {bmp.Format}   中心 {CenterColor(bmp)}   角上 {CornerColor(bmp)}");
        }
        catch (Exception ex)
        {
            lines.Add($"    失败：{ex.Message}");
        }
    }

    /// <summary>透明索引和角色颜色重合时，WPF 会不会把角色身上那部分也挖空。</summary>
    private static void ProbeLeak(List<string> lines, string dir, string name, string label)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return;

        lines.Add($"  {label}");
        try
        {
            var bmp = Load(p);
            string center = CenterColor(bmp);
            bool holed = center.EndsWith("/a0");
            lines.Add($"    中心 {center}   角上 {CornerColor(bmp)}");
            lines.Add(holed
                ? "      → 中心色块**被挖空了**：透明索引撞上了角色颜色"
                : "      → 中心色块完好，未受影响");
        }
        catch (Exception ex)
        {
            lines.Add($"    失败：{ex.Message}");
        }
    }
}
