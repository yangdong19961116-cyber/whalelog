using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhaleLog.Services;

/// <summary>
/// 形象与图标素材的加载。
///
/// 查找顺序（把 PaperTodo「程序目录放 ico 就换图标」的约定推广到全部素材）：
///   1. 程序旁边的 Assets\ 目录 —— 外部覆盖，换素材不用重新编译
///   2. 编译进程序集的嵌入资源 —— 出厂默认
///
/// 外部文件缺失、格式不对、读到一半坏掉，一律安静回落到内置：
/// 素材出问题绝不该让程序起不来。清单见 <see cref="AssetCatalog"/>。
/// </summary>
public static class IconAssets
{
    /// <summary>
    /// 图片解码的目标高度。悬浮窗最大显示约 270px（1.6 倍缩放），留一倍余量给高 DPI。
    ///
    /// 只有源图明显大于它时才限制解码 —— 否则一张 4000×4000 的图会白占几十 MB 内存；
    /// 小图不设，否则会被插值放大再缩回去，白白变模糊。
    /// </summary>
    private const int DecodeTarget = 540;

    private static readonly Dictionary<string, ImageSource> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次会话里成功从外部文件加载的素材：逻辑名 → 文件路径。</summary>
    private static readonly Dictionary<string, string> ExternalHits =
        new(StringComparer.OrdinalIgnoreCase);

    private static Icon? _cachedIcon;

    /// <summary>哪些素材当前来自外部文件。自检报告和界面提示用。</summary>
    public static IReadOnlyDictionary<string, string> LoadedFromExternal => ExternalHits;

    // ==================================================================
    // 图片素材
    // ==================================================================

    /// <summary>
    /// 加载一个图片素材（外部优先）。
    /// 取不到返回 null —— 调用方请保留原值，不要赋 null 把界面清空。
    /// </summary>
    public static ImageSource? Load(string name)
    {
        if (ImageCache.TryGetValue(name, out var cached)) return cached;

        ImageSource? image = null;
        var external = AssetCatalog.FindExternal(name);

        if (external != null)
        {
            image = TryLoadImageFile(external);
            if (image != null)
            {
                ExternalHits[name] = external;
            }
            else
            {
                // 静默回落会让用户以为"外面那张图换上了"，所以留一条日志
                CrashLog.Note($"[素材] 外部素材读不出来，已回落到内置图：{external}");
            }
        }

        image ??= TryLoadEmbedded(name);

        if (image != null) ImageCache[name] = image;
        return image;
    }

    /// <summary>丢掉所有缓存，下次访问重新解析。「重新加载素材」用它。</summary>
    public static void Reload()
    {
        ImageCache.Clear();
        ExternalHits.Clear();
        _cachedIcon = null;
    }

    private static ImageSource? TryLoadImageFile(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            // OnLoad：立刻读完并释放文件句柄。否则素材文件会被程序占住，
            // 用户想替换时得到「文件正在使用中」。
            image.CacheOption = BitmapCacheOption.OnLoad;
            // 无视 WPF 按 URI 的图片缓存，否则改了文件内容还是显示旧图
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

            var height = ProbePixelHeight(path);
            if (height.HasValue && height.Value > DecodeTarget * 2)
                image.DecodePixelHeight = DecodeTarget;

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // 不是图片、格式不支持、文件被独占 —— 都走回落
            return null;
        }
    }

    /// <summary>
    /// 只读一下图片尺寸（DelayCreation 不解码像素，很快），用来决定要不要限制解码尺寸。
    /// </summary>
    private static int? ProbePixelHeight(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames.Count > 0 ? decoder.Frames[0].PixelHeight : null;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryLoadEmbedded(string name)
    {
        var entry = AssetCatalog.Find(name);
        if (entry == null) return null;

        try
        {
            using var stream = System.Windows.Application
                .GetResourceStream(new Uri(entry.EmbeddedUri))?.Stream;
            if (stream == null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // pack:// 资源取不到（例如没有 Application 实例）时返回 null
            return null;
        }
    }

    // ==================================================================
    // 图标
    // ==================================================================

    /// <summary>托盘 / 窗口 / 快捷方式用的图标。外部优先，再内置，最后兜底系统图标。</summary>
    public static Icon LoadAppIcon()
    {
        if (_cachedIcon != null) return _cachedIcon;

        // exe 旁边的 whalelog.ico 是历史约定，继续排在 Assets\ 前面
        foreach (var candidate in new[] { LegacyExeIconPath(), AssetCatalog.FindExternal("icon") })
        {
            var icon = TryLoadIconFile(candidate);
            if (icon == null) continue;

            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                ExternalHits["icon"] = candidate;

            _cachedIcon = icon;
            return icon;
        }

        _cachedIcon = TryLoadIconEmbedded() ?? SystemIcons.Application;
        return _cachedIcon;
    }

    private static string LegacyExeIconPath()
    {
        try { return Path.Combine(AppContext.BaseDirectory, "whalelog.ico"); }
        catch { return ""; }
    }

    private static Icon? TryLoadIconFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Icon? TryLoadIconEmbedded()
    {
        var entry = AssetCatalog.Find("icon");
        if (entry == null) return null;

        try
        {
            using var stream = System.Windows.Application
                .GetResourceStream(new Uri(entry.EmbeddedUri))?.Stream;
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把图标落一份到磁盘，供快捷方式的 IconLocation 使用 —— 快捷方式只认文件路径，
    /// 读不到编译进程序集的嵌入资源。
    /// </summary>
    public static string? EnsureIconFile()
    {
        try
        {
            // 1) 用户自己换的（Assets\ 或 exe 旁边）优先
            var external = AssetCatalog.FindExternal("icon") ?? LegacyExeIconPath();
            if (!string.IsNullOrWhiteSpace(external) && File.Exists(external)) return external;

            // 2) 之前从嵌入资源导出过的那份
            var cached = Path.Combine(AppPaths.Root, "whalelog.ico");
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

            // 3) 从嵌入资源导出一份
            var entry = AssetCatalog.Find("icon");
            if (entry == null) return null;

            Directory.CreateDirectory(AppPaths.Root);
            using var stream = System.Windows.Application
                .GetResourceStream(new Uri(entry.EmbeddedUri))?.Stream;
            if (stream == null) return null;

            using (stream)
            using (var file = File.Create(cached))
            {
                stream.CopyTo(file);
            }

            return cached;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>窗口图标（WPF 用）。</summary>
    public static ImageSource? LoadWindowIcon() => Load("icon");

    /// <summary>关于页的预览图。</summary>
    public static ImageSource? LoadPreviewImage() => Load("preview");

    // ==================================================================
    // 导出内置素材
    // ==================================================================

    /// <summary>
    /// 首次运行时把内置素材导出到 Assets\，让用户看得见、知道该换哪个文件。
    ///
    /// **已存在的文件一律不覆盖** —— 那可能是用户自己换的。
    /// dir 传 null 时用默认的素材目录；打包脚本会指定发布目录下的 Assets。
    /// 返回本次实际写出的文件路径。
    /// </summary>
    public static List<string> ExportDefaults(string? dir = null)
    {
        dir ??= AppPaths.AssetsDir;
        var written = new List<string>();

        try
        {
            Directory.CreateDirectory(dir);

            foreach (var entry in AssetCatalog.All)
            {
                // 任何一个候选名已经存在，就说明这个素材用户动过，跳过
                if (AssetCatalog.FindExternal(entry.Name, dir) != null) continue;

                var target = Path.Combine(dir, entry.Files[0]);
                if (File.Exists(target)) continue;

                if (CopyEmbeddedTo(entry.EmbeddedUri, target))
                    written.Add(target);
            }

            var readme = Path.Combine(dir, "素材说明.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme, BuildReadme(), new UTF8Encoding(true));
                written.Add(readme);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Note($"[素材] 导出内置素材失败：{ex.Message}");
        }

        return written;
    }

    private static bool CopyEmbeddedTo(string uri, string target)
    {
        try
        {
            using var stream = System.Windows.Application
                .GetResourceStream(new Uri(uri))?.Stream;
            if (stream == null) return false;

            using (stream)
            using (var file = File.Create(target))
            {
                stream.CopyTo(file);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildReadme()
    {
        var sb = new StringBuilder();
        sb.AppendLine("鲸记 WhaleLog · 外部素材目录");
        sb.AppendLine("==============================");
        sb.AppendLine();
        sb.AppendLine("放在这个目录里的文件会优先于程序内置的素材，换素材不用重新编译。");
        sb.AppendLine("删掉对应文件，下次启动就恢复成内置的默认素材。");
        sb.AppendLine();
        sb.AppendLine("------------------------------");

        foreach (var entry in AssetCatalog.All)
        {
            sb.AppendLine();
            sb.AppendLine(entry.Files[0]);
            sb.AppendLine($"  {entry.Title}");
            foreach (var alias in entry.Files.Skip(1))
                sb.AppendLine($"  也可以命名为 {alias}");
            sb.AppendLine($"    {entry.Spec}");
        }

        sb.AppendLine();
        sb.AppendLine("------------------------------");
        sb.AppendLine();
        sb.AppendLine("动态素材（小鲸鱼动画）");
        sb.AppendLine();
        sb.AppendLine("  在 anim\\ 下放精灵图动画，桌面上的小鲸鱼就能动起来：");
        sb.AppendLine();
        sb.AppendLine("    anim\\clickBounce\\sheet.png + sheet.json   单击时弹跳");
        sb.AppendLine("    anim\\blink\\sheet.png + sheet.json         闲置时眨眼（4~9 秒一次）");
        sb.AppendLine("    anim\\alert\\sheet.png + sheet.json         到点提醒时播（可选）");
        sb.AppendLine("    anim\\base.png                             与精灵图同画布的静态本体");
        sb.AppendLine();
        sb.AppendLine("  一个动作一个子目录，目录名就是动作名。放完在托盘菜单点「重新加载素材」即可生效。");
        sb.AppendLine();
        sb.AppendLine("  两条必须守住的规则：");
        sb.AppendLine("    · 画布 720×720，角色高 600，横向中心钉在 x=360、脚底钉在 y=660；");
        sb.AppendLine("      帧间只允许整体上下移动和绕该点缩放，横向中心绝对不能漂。");
        sb.AppendLine("    · 角色任何一帧都不能贴到（更不能越过）画布边缘，至少留 2px 余量。");
        sb.AppendLine("      纵向拉伸会把头顶往上顶，所以跳跃幅度要按这个公式倒推：");
        sb.AppendLine("          头顶 y = 661 − round(600 × 纵向缩放) + 垂直位移   ≥ 2");
        sb.AppendLine("      第一版样例就是漏算了拉伸，最高帧的头顶被裁掉了十几像素。");
        sb.AppendLine();
        sb.AppendLine("  帧表 sheet.json 的字段、排帧方式、各类报错的排查顺序，");
        sb.AppendLine("  见《动态素材规格.md》—— 打包版放在程序目录下，源码仓库里在 docs\\ 下。");
        sb.AppendLine("  交付前用 tools\\check_anim_assets.py 校验一遍，它能直接指出是哪一帧漂了、哪一帧被裁了。");
        sb.AppendLine();
        sb.AppendLine("  不想让小鲸鱼动？托盘菜单 →「小鲸鱼动画」可以随时关掉，素材文件不用删。");
        sb.AppendLine();
        sb.AppendLine("------------------------------");
        sb.AppendLine();
        sb.AppendLine("生效方式");
        sb.AppendLine("  图片素材：右键托盘图标 → 重新加载素材");
        sb.AppendLine("  图标 whalelog.ico：需要重启程序，资源管理器缓存了旧图标");
        sb.AppendLine();
        sb.AppendLine("几点注意");
        sb.AppendLine("  · PNG 必须带透明通道，否则会显示成一块方形底色。");
        sb.AppendLine("  · 形象图按 Uniform 等比缩放，不用刻意裁成固定尺寸；");
        sb.AppendLine("    **但表情差分（whale-hover.png）必须和 whale.png 同画布、逐像素对齐** ——");
        sb.AppendLine("    鼠标悬浮时会整张换成它，画布或位置差一点，鼠标一移上去角色就会跳一下。");
        sb.AppendLine("    手里只有白底的新图也没关系：");
        sb.AppendLine("        python tools\\prepare_expression.py 新图.png out\\whale-hover.png");
        sb.AppendLine("    它会抠掉背景、对齐到你们那张基准图，并报出对齐误差（IoU，0.98 以上算合格）。");
        sb.AppendLine("  · 素材缺失或读不出来会自动回落成内置图，不会让程序起不来。");
        sb.AppendLine();

        return sb.ToString();
    }
}
