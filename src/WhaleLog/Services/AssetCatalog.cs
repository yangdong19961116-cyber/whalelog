using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 素材清单：逻辑名 ↔ 外部候选文件名 ↔ 内置资源。
///
/// 这一层刻意不碰 WPF（没有 BitmapImage、不解析 pack://），
/// 于是自测能直接编译它来验证「外部优先、缺失回落」的判定逻辑 ——
/// 界面代码是测不了的，纯逻辑可以。
/// </summary>
public static class AssetCatalog
{
    /// <summary>一个素材的完整定义。</summary>
    public sealed record Entry(
        string Name,
        string Title,
        string[] Files,
        string EmbeddedUri,
        string Spec);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry(
            "whale",
            "桌面小鲸鱼形象",
            // anim\base.png 排在最后：它是「动态素材」那一套里的静态本体，
            // 和精灵图同一块画布。用户没放动态素材时它不存在，取值不受影响；
            // 放了的话（且没另给 whale.png）静止状态就用它 —— 这样
            // 「静止 → 播放第一帧」不会因为画布不同而跳一下。
            new[] { "whale.png", "DSniang1.png", "anim/base.png" },
            "pack://application:,,,/Resources/Assets/DSniang1.png",
            "PNG，带透明通道。正方形画布、角色居中；程序按 Uniform 等比缩放显示，\n" +
            "    不用刻意裁成固定尺寸，但同一套形象若有多张差分，画布必须一致。\n" +
            "    要做动画：把同画布的静态本体放成 anim\\base.png（详见动态素材说明）。"),

        new Entry(
            "whale-head",
            "小头像（收起态 / 标题栏 / 空状态）",
            new[] { "whale-head.png", "head.png" },
            "pack://application:,,,/Resources/Assets/whale-head.png",
            "PNG，带透明通道。正方形；实际显示在 26×26 到 52×52 之间，建议 256×256 以上。"),

        new Entry(
            "icon",
            "程序与托盘图标",
            new[] { "whalelog.ico", "app.ico" },
            "pack://application:,,,/Resources/Assets/whalelog.ico",
            "ICO，建议内含 16 / 32 / 48 / 64 / 128 / 256 多个尺寸。\n" +
            "    换这个需要重启程序，资源管理器缓存了旧图标。"),

        new Entry(
            "preview",
            "关于页预览图",
            new[] { "whalelog-256.png", "preview.png" },
            "pack://application:,,,/Resources/Assets/whalelog-256.png",
            "PNG，建议 256×256，可以有背景。"),
    };

    public static Entry? Find(string name)
        => All.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 在指定目录里找这个素材的外部文件。目录传 null 时用默认的外部素材目录。
    /// 按候选文件名依次尝试，取不到返回 null。
    /// </summary>
    public static string? FindExternal(string name, string? dir = null)
    {
        var entry = Find(name);
        if (entry == null) return null;

        dir ??= AppPaths.AssetsDir;

        foreach (var file in entry.Files)
        {
            try
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 文件名非法（被塞了路径分隔符之类）就跳过，继续试下一个候选名
            }
        }

        return null;
    }

    /// <summary>某个素材当前的来源描述，用于自检报告和界面提示。</summary>
    public static string Describe(string name, string? dir = null)
    {
        var entry = Find(name);
        if (entry == null) return $"未知素材：{name}";

        var external = FindExternal(name, dir);
        return external != null
            ? $"{entry.Title} ← {Path.GetFileName(external)}"
            : $"{entry.Title}（内置默认）";
    }
}
