using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 动态素材（精灵图动画）的发现：扫「素材目录\anim\」下的一堆动作。
///
/// 目录约定（刻意简单，因为这份约定要发给做素材的人看）：
///
///     Assets\anim\base.png              静态本体，和精灵图同一套画布（可选）
///     Assets\anim\clickBounce\sheet.png 单击弹跳
///     Assets\anim\clickBounce\sheet.json
///     Assets\anim\blink\sheet.png       闲置眨眼
///     Assets\anim\blink\sheet.json
///     Assets\anim\alert\sheet.png       到点提醒（可选）
///     Assets\anim\alert\sheet.json
///
/// **一个动作 = 一个子目录，目录名就是动作名**（大小写、下划线、连字符会归一化）。
/// 不用"在素材目录里散着一堆 json"的写法：动作名和文件名的对应关系一旦靠猜，
/// 出问题时（"我明明放了，怎么没反应"）就没有任何东西可以对着查。
///
/// 与 <see cref="AssetCatalog"/> 一样，这个类不碰 WPF，自测能直接编译它。
/// </summary>
public sealed class AnimLibrary
{
    /// <summary>素材目录下放动态素材的子目录名。</summary>
    public const string FolderName = "anim";

    /// <summary>静态本体的文件名（与精灵图同画布）。</summary>
    public const string BaseImageName = "base.png";

    /// <summary>帧表文件名。目录里有多个 json 时优先用它。</summary>
    public const string ManifestName = "sheet.json";

    /// <summary>静态形象的鲸鱼框高度（设计像素）。</summary>
    public const double StaticWhaleBoxHeight = 168;

    /// <summary>
    /// 角色在屏幕上的设计高度。
    ///
    /// 静态形象是 610 画布塞进 168 的框、角色占 600 高 → 168 × 600 ÷ 610 ≈ 165。
    /// 换成精灵图（720 画布、角色还是 600）之后，要保住的正是这个数 ——
    /// 保住了，用户就完全感觉不到形象被换过。
    /// </summary>
    public const double FloatingCharacterDesignHeight = 165;

    private readonly Dictionary<string, AnimSheet> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dirs = new(StringComparer.Ordinal);
    private readonly List<string> _problems = new();

    /// <summary>
    /// 动作键 → 角色。**必须在扫描时从目录名记下来，不能事后从动作键推算** ——
    /// 目录名叫 <c>idle</c> 时动作键会被归一化成 <c>blink</c>（历史行为），
    /// 事后推算会把它的角色搞错。
    /// </summary>
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);

    private AnimLibrary() { }

    /// <summary>扫到的、能读出来的动作，按名字排序。</summary>
    public IReadOnlyList<string> ActionNames { get; private set; } = Array.Empty<string>();

    /// <summary>与精灵图同画布的静态本体，取不到为 null。</summary>
    public string? BaseImagePath { get; private set; }

    /// <summary>扫过的目录（诊断报告里写出来，用户才知道程序到底看了哪里）。</summary>
    public string ScanDir { get; private set; } = "";

    /// <summary>读不出来 / 有问题的动作，形如「blink：缺字段 count」。不影响其他动作。</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>一个动作都没有。界面据此退回静态形象。</summary>
    public bool IsEmpty => _actions.Count == 0;

    /// <summary>有能用的动态素材。</summary>
    public bool HasAny => _actions.Count > 0;

    public AnimSheet? Find(string action)
        => _actions.TryGetValue(AnimActions.Canonical(action), out var sheet) ? sheet : null;

    /// <summary>这个动作的角色（<see cref="AnimActions.RoleOf"/> 的结果）。</summary>
    public string RoleOf(string action)
        => _roles.TryGetValue(AnimActions.Canonical(action), out var role) ? role : "";

    /// <summary>
    /// 某个角色下的全部动作键，按名字排序。
    ///
    /// 界面靠它决定"点击时随机挑一个"、"闲下来随机挑一个"。
    /// 一个都没有时返回空列表 —— 调用方据此走降级分支，而不是硬编码假设它存在。
    /// </summary>
    public IReadOnlyList<string> ActionsWithRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return Array.Empty<string>();

        return _actions.Keys
            .Where(k => _roles.TryGetValue(k, out var r) && r.Equals(role, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>每个角色各有多少个动作。诊断报告用。</summary>
    public IReadOnlyDictionary<string, int> RoleCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var role in _roles.Values)
        {
            if (role.Length == 0) continue;
            counts[role] = counts.TryGetValue(role, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    /// <summary>能认出角色的动作数（相对于"自定义动作"）。</summary>
    public int RecognizedRoleCount => _roles.Values.Count(r => r.Length > 0);

    /// <summary>视频动作的数量（这些需要 ffmpeg 才能播）。</summary>
    public int VideoCount => _actions.Values.Count(s => s.IsVideo);

    /// <summary>精灵图动作的数量。</summary>
    public int SheetCount => _actions.Values.Count(s => !s.IsVideo);

    /// <summary>某个动作的素材目录（帧表里的 sheet 是相对它解析的）。</summary>
    public string? DirOf(string action)
        => _dirs.TryGetValue(AnimActions.Canonical(action), out var dir) ? dir : null;

    /// <summary>这个动作的媒体文件完整路径（精灵图 PNG 或视频 webm，由帧表的 type 决定）。
    /// 取不到返回 null。</summary>
    public string? MediaPath(string action)
    {
        var sheet = Find(action);
        var dir = DirOf(action);
        if (sheet == null || dir == null || sheet.MediaFile.Length == 0) return null;

        try { return Path.Combine(dir, sheet.MediaFile); }
        catch { return null; }
    }

    /// <summary>
    /// 所有动作里最大的那个显示框高度（设计像素）。
    ///
    /// 取最大值而不是第一个：悬浮窗只有一个鲸鱼框，几个动作的帧表万一写得不一样，
    /// 按小的那个算就会把大的那个裁掉 —— 宁可留白，不可裁切。
    /// 全部动作都没写 characterHeight 时返回 null（由调用方退回静态尺寸）。
    /// </summary>
    public double? DisplayBoxHeight(double designCharacterHeight)
    {
        double? max = null;
        foreach (var name in ActionNames)
        {
            var h = _actions[name].DisplayBoxHeight(designCharacterHeight);
            if (h.HasValue) max = max.HasValue ? Math.Max(max.Value, h.Value) : h.Value;
        }
        return max;
    }

    // ==================================================================
    // 扫描
    // ==================================================================

    /// <summary>
    /// 扫一遍动态素材目录。<paramref name="assetDir"/> 传 null 时用默认素材目录。
    ///
    /// **任何错误都只是"少一个动作"，绝不抛异常** —— 素材坏了顶多不播动画，
    /// 不该让悬浮窗（甚至整个程序）起不来。每个失败都记进 <see cref="Problems"/>，
    /// 诊断自检会把它们列出来。
    /// </summary>
    public static AnimLibrary Scan(string? assetDir = null)
    {
        var lib = new AnimLibrary();

        try
        {
            lib.ScanDir = Path.Combine(assetDir ?? AppPaths.AssetsDir, FolderName);
        }
        catch (Exception ex)
        {
            lib._problems.Add($"拼不出动态素材目录：{ex.Message}");
            return lib;
        }

        try
        {
            if (!Directory.Exists(lib.ScanDir)) return lib;

            // 静态本体直接放在 anim\ 下，和精灵图同一套画布。
            // 有这个文件时静止状态也用它 —— 这样"静止 → 播放第一帧"不会跳。
            var basePath = Path.Combine(lib.ScanDir, BaseImageName);
            if (File.Exists(basePath)) lib.BaseImagePath = basePath;

            foreach (var dir in Directory.GetDirectories(lib.ScanDir)
                         .OrderBy(d => PriorityOf(Path.GetFileName(d)))
                         .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var dirName = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(dirName)) lib.LoadAction(dir, dirName);
            }
        }
        catch (Exception ex)
        {
            lib._problems.Add($"扫描 {lib.ScanDir} 失败：{ex.Message}");
        }

        lib.ActionNames = lib._actions.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return lib;
    }

    /// <summary>
    /// 扫描顺序的优先级：目录名和规范动作名完全一致（不分大小写）的排前面。
    ///
    /// 归一化之后撞车的两份里只有一份能生效。按纯字母序决定的话，
    /// 「clickBounce」和「Click-Bounce」同时存在时赢的是后者 —— 用户显然不是这个意思，
    /// 命中规范名的那份才是他真正在用的那个。
    /// </summary>
    private static int PriorityOf(string dirName)
        => dirName.Equals(AnimActions.Canonical(dirName), StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    /// <summary>读一个动作目录。失败只记问题，不抛。</summary>
    private void LoadAction(string dir, string dirName)
    {
        try
        {
            var sheet = ReadManifest(dir, dirName);
            if (sheet == null) return;

            var key = AnimActions.Canonical(dirName);

            if (_actions.ContainsKey(key))
            {
                // 目录名归一化之后撞车（blink 和 Blink_ 会撞），只认先扫到的那个。
                // 静默丢弃会让用户以为"两个都生效了"。
                _problems.Add($"{dirName}：动作名和已有的「{key}」重复，这一份被跳过");
                return;
            }

            _actions[key] = sheet;
            _dirs[key] = dir;
            // 角色从**目录名**认，不是从归一化后的动作键认 —— 目录叫 idle 时
            // 动作键会变成 blink，从键反推会把角色搞错。
            _roles[key] = AnimActions.RoleOf(dirName);
        }
        catch (Exception ex)
        {
            _problems.Add($"{dirName}：读不出来（{ex.Message}）");
        }
    }

    /// <summary>读帧表并把问题记进 Problems；拿不到可用的帧表时返回 null。</summary>
    private AnimSheet? ReadManifest(string dir, string dirName)
    {
        // 优先 sheet.json；目录里只有一个 json 时也认（免得"我改了个名字就不认了"）
        var manifests = Directory.GetFiles(dir, "*.json");
        if (manifests.Length == 0)
        {
            _problems.Add($"{dirName}：目录里没有帧表（{ManifestName}）");
            return null;
        }

        var manifestPath = manifests.FirstOrDefault(
            m => Path.GetFileName(m).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            ?? manifests.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).First();

        var result = AnimSheet.Parse(File.ReadAllText(manifestPath));
        if (result.Sheet == null)
        {
            _problems.Add($"{dirName}：{result.Error}");
            return null;
        }

        // 帧表点名的媒体文件得真的在（精灵图 PNG 或视频 webm）
        if (result.Sheet.MediaFile.Length == 0 ||
            !File.Exists(Path.Combine(dir, result.Sheet.MediaFile)))
        {
            _problems.Add($"{dirName}：找不到{DescribeKind(result.Sheet)} {result.Sheet.MediaFile}");
            return null;
        }

        foreach (var w in result.Warnings) _problems.Add($"{dirName}：{w}");
        return result.Sheet;
    }

    private static string DescribeKind(AnimSheet sheet) => sheet.IsVideo ? "视频" : "精灵图";

    /// <summary>
    /// 诊断报告用的一段话。要说清"看了哪个目录、认出了几个动作、哪个没认出来"——
    /// 用户报障时最想知道的正是这三件事。
    /// </summary>
    public string Describe()
    {
        var lines = new List<string> { $"动态素材目录：{ScanDir}" };

        if (!Directory.Exists(ScanDir))
        {
            lines.Add("目录不存在 —— 用静态形象。要开动画就建 anim\\<动作名>\\ 放 sheet.png + sheet.json。");
            return string.Join(Environment.NewLine, lines);
        }

        if (IsEmpty)
        {
            lines.Add("没扫到能用的动作 —— 用静态形象。");
        }
        else
        {
            var video = VideoCount;
            var kind = video == 0 ? "精灵图"
                     : SheetCount == 0 ? "透明视频"
                     : $"精灵图 {SheetCount} + 透明视频 {video}";
            lines.Add($"启用 {ActionNames.Count} 个动作（{kind}）");

            // 动作少的时候逐个列出来；dsh 那种一百多个列出来没人看得下去
            if (ActionNames.Count <= 12)
                lines.Add(string.Join("、", ActionNames));

            var counts = RoleCounts();
            if (counts.Count > 0)
            {
                lines.Add("按角色分：" + string.Join("、", counts
                    .OrderByDescending(p => p.Value)
                    .Select(p => $"{p.Key}×{p.Value}")));
            }

            var custom = ActionNames.Count - RecognizedRoleCount;
            if (custom > 0)
                lines.Add($"{custom} 个动作认不出角色（前缀不在已知角色里，界面不会主动播它们）");

            var box = DisplayBoxHeight(FloatingCharacterDesignHeight);
            lines.Add(box.HasValue
                ? $"鲸鱼框按帧表算得 {box.Value:0.#}px（静态形象是 {StaticWhaleBoxHeight:0}px）"
                : "帧表都没写 characterHeight，鲸鱼框保持静态尺寸");

            lines.Add(BaseImagePath != null
                ? $"静态本体：{Path.GetFileName(BaseImagePath)}（与精灵图同画布）"
                : "没有 anim\\base.png —— 静止时仍用原来的形象图；画布不一致的话，切换动作时能看到尺寸跳变");

            if (video > 0)
            {
                lines.Add(FfmpegLocator.Describe());
            }
        }

        foreach (var p in Problems) lines.Add("· " + p);

        return string.Join(Environment.NewLine, lines);
    }
}
