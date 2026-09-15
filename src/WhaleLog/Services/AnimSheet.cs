using System.Text.Json;

namespace WhaleLog.Services;

/// <summary>
/// 精灵图动画的帧表（sheet.json）—— 解析、校验、网格切帧的**纯逻辑**。
///
/// 刻意不碰 WPF（不引 BitmapImage / CroppedBitmap，甚至不解析图片），
/// 于是自测能直接编译这一个文件来验证解析、切帧、降级这些分支 ——
/// 界面代码是测不了的，纯逻辑可以。
///
/// 规格（画布、锚点规则、帧表字段、交付前自检）见仓库里的 docs\动态素材规格.md，
/// 校验脚本是 tools\check_anim_assets.py。
/// </summary>
public sealed class AnimSheet
{
    // ------------------------------------------------------------------
    // 解析结果的边界（防止手写错的帧表把程序拖死）
    // ------------------------------------------------------------------

    /// <summary>单边最大帧尺寸。再大就不是给悬浮窗用的素材了。</summary>
    public const int MaxFrameSide = 4096;

    /// <summary>最多帧数。悬浮窗动画不会有几百帧。</summary>
    public const int MaxCount = 512;

    /// <summary>没写 fps 时用的帧率。</summary>
    public const double DefaultFps = 24;

    /// <summary>允许的帧率区间。悬浮窗很小，超过 30fps 是纯浪费（实测渲染时钟约 64Hz）。</summary>
    public const double MinFps = 1;
    public const double MaxFps = 30;

    /// <summary>动画名（帧表里的 name，纯描述用）。</summary>
    public string Name { get; private init; } = "";

    public int Version { get; private init; } = 1;

    /// <summary>容器类型。目前只认 "sheet"。</summary>
    public string SheetType { get; private init; } = "sheet";

    /// <summary>精灵图文件名（相对帧表所在目录）。</summary>
    public string Sheet { get; private init; } = "";

    public int FrameWidth { get; private init; }
    public int FrameHeight { get; private init; }
    public int Cols { get; private init; }
    public int Rows { get; private init; }
    public int Count { get; private init; }
    public double Fps { get; private init; }
    public bool Loop { get; private init; }

    /// <summary>锚点：每帧角色的横向中心与脚底必须落在这里。</summary>
    public double AnchorX { get; private init; }
    public double AnchorY { get; private init; }

    /// <summary>
    /// 角色在画布里的实际高度（像素）。用它和帧高算「画布该显示多大」，
    /// 才能保证换成精灵图之后角色在屏幕上的大小一点不变。
    /// 0 表示帧表没写 —— 见 <see cref="DisplayBoxHeight"/>。
    /// </summary>
    public int CharacterHeight { get; private init; }

    /// <summary>播完之后回到哪里（"base" = 静态本体）。</summary>
    public string ReturnTo { get; private init; } = "base";

    /// <summary>配套音效文件名，留空表示不放声音。</summary>
    public string Sound { get; private init; } = "";

    /// <summary>帧表里显式写了 characterHeight（写没写会走不同的显示策略）。</summary>
    public bool HasCharacterHeight => CharacterHeight > 0;

    /// <summary>一帧播多久。</summary>
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1.0 / Fps);

    /// <summary>整段动画播完要多久。</summary>
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(Count / Fps);

    private AnimSheet() { }

    // ==================================================================
    // 解析
    // ==================================================================

    /// <summary>
    /// 解析一份帧表。**任何字段有问题都返回可读的中文原因**，
    /// 因为这份文件是发给做素材的人看的 —— "JSON 反序列化失败" 对他是没有信息量的。
    /// </summary>
    public static AnimSheetParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return AnimSheetParseResult.Failure("帧表是空的");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                // 允许注释和尾逗号：手写帧表时很容易带上，不该因此报废
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return AnimSheetParseResult.Failure($"不是合法的 JSON：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AnimSheetParseResult.Failure("帧表的顶层必须是一个对象 { }");

            var warnings = new List<string>();

            // ---- 容器类型 ----
            var type = ReadString(root, "type");
            if (!string.IsNullOrEmpty(type) && !type.Equals("sheet", StringComparison.OrdinalIgnoreCase))
            {
                if (type.Equals("sequence", StringComparison.OrdinalIgnoreCase))
                    return AnimSheetParseResult.Failure(
                        "type=sequence（PNG 序列）暂不支持，请改成 type=sheet 的精灵图");
                return AnimSheetParseResult.Failure($"不认识的 type：「{type}」，目前只支持 \"sheet\"");
            }

            // ---- 精灵图文件名 ----
            var sheetFile = ReadString(root, "sheet");
            if (string.IsNullOrWhiteSpace(sheetFile))
                return AnimSheetParseResult.Failure("缺字段 sheet（精灵图文件名）");
            // 只认文件名，不许带路径 —— 否则帧表能指到任意位置去
            if (sheetFile.Contains("..", StringComparison.Ordinal) ||
                sheetFile.IndexOfAny(new[] { '/', '\\' }) >= 0)
                return AnimSheetParseResult.Failure($"sheet 只能是同目录下的文件名，不能带路径：「{sheetFile}」");

            // ---- 几何 ----
            int fw = ReadInt(root, "frameWidth", 0);
            int fh = ReadInt(root, "frameHeight", 0);
            if (fw <= 0 || fh <= 0)
                return AnimSheetParseResult.Failure($"帧尺寸不合法：{fw}×{fh}（frameWidth / frameHeight 必须是正整数）");
            if (fw > MaxFrameSide || fh > MaxFrameSide)
                return AnimSheetParseResult.Failure($"帧尺寸 {fw}×{fh} 超过上限 {MaxFrameSide}");

            int cols = ReadInt(root, "cols", 0);
            int rows = ReadInt(root, "rows", 0);
            if (cols <= 0 || rows <= 0)
                return AnimSheetParseResult.Failure($"网格不合法：{cols} 列 × {rows} 行（cols / rows 必须是正整数）");

            int count = ReadInt(root, "count", 0);
            if (count <= 0)
                return AnimSheetParseResult.Failure("缺字段 count（帧数），或者它不是一个正整数");
            if (count > MaxCount)
                return AnimSheetParseResult.Failure($"帧数 {count} 超过上限 {MaxCount}");
            if (cols * rows < count)
                return AnimSheetParseResult.Failure(
                    $"网格放不下 {count} 帧：{cols} 列 × {rows} 行 = {cols * rows}");

            // 网格比帧数多出来的那些格子是允许的（方便排版成 4×2 放 6 帧），
            // 但要在日志里说一声，免得做素材的人以为程序"多数了一格"。
            if (cols * rows > count)
                warnings.Add($"网格 {cols}×{rows}={cols * rows} 比帧数 {count} 多，多出来的格子不会被播放");

            // ---- 帧率 ----
            double fps = ReadDouble(root, "fps", 0);
            if (fps <= 0)
            {
                fps = DefaultFps;
                warnings.Add($"帧表没写 fps，按默认 {DefaultFps:0} fps 播放");
            }
            else if (fps < MinFps || fps > MaxFps)
            {
                var clamped = Math.Clamp(fps, MinFps, MaxFps);
                warnings.Add($"fps={fps:0.##} 超出 {MinFps:0}–{MaxFps:0} 的范围，播放时按 {clamped:0.##} 处理");
                fps = clamped;
            }

            // ---- 锚点 ----
            double anchorX = fw / 2.0;
            double anchorY = fh;
            if (root.TryGetProperty("anchor", out var anchorEl) && anchorEl.ValueKind == JsonValueKind.Object)
            {
                anchorX = ReadDouble(anchorEl, "x", anchorX);
                anchorY = ReadDouble(anchorEl, "y", anchorY);
            }
            else
            {
                warnings.Add($"帧表没写 anchor，按「横向居中、脚底贴下边」处理（x={anchorX:0.#}, y={anchorY:0.#}）");
            }

            if (anchorX < 0 || anchorX > fw || anchorY < 0 || anchorY > fh)
                return AnimSheetParseResult.Failure(
                    $"anchor ({anchorX:0.#}, {anchorY:0.#}) 落在画布 {fw}×{fh} 之外");

            // ---- 角色高度 ----
            int charH = ReadInt(root, "characterHeight", 0);
            if (charH <= 0)
            {
                warnings.Add("帧表没写 characterHeight —— 显示时按静态形象的尺寸走，" +
                             "角色在画布里的占比会和原来不一致，建议按规格补齐");
            }
            else if (charH > fh)
            {
                warnings.Add($"characterHeight={charH} 比帧高 {fh} 还大，按帧高处理");
                charH = fh;
            }

            var sheet = new AnimSheet
            {
                Name = ReadString(root, "name"),
                Version = ReadInt(root, "version", 1),
                SheetType = string.IsNullOrEmpty(type) ? "sheet" : type.ToLowerInvariant(),
                Sheet = sheetFile,
                FrameWidth = fw,
                FrameHeight = fh,
                Cols = cols,
                Rows = rows,
                Count = count,
                Fps = fps,
                Loop = ReadBool(root, "loop"),
                AnchorX = anchorX,
                AnchorY = anchorY,
                CharacterHeight = charH,
                ReturnTo = ReadString(root, "returnTo") is { Length: > 0 } r ? r : "base",
                Sound = ReadString(root, "sound"),
            };

            return AnimSheetParseResult.Success(sheet, warnings);
        }
    }

    // ==================================================================
    // 切帧
    // ==================================================================

    /// <summary>
    /// 第 <paramref name="index"/> 帧在精灵图里的矩形（像素）。
    ///
    /// 行优先：先从左到右填满一行，再换下一行 —— 4 列 2 行的 8 帧就是
    /// 第 0 帧在左上、第 3 帧在右上、第 4 帧在第二行左边。
    /// 索引越界返回 false（调用方据此停播，而不是抛异常）。
    /// </summary>
    public bool TryFrameRect(int index, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (index < 0 || index >= Count) return false;

        x = index % Cols * FrameWidth;
        y = index / Cols * FrameHeight;
        width = FrameWidth;
        height = FrameHeight;
        return true;
    }

    /// <summary>精灵图按帧表算应该有多大。用来在加载时核对实际像素尺寸。</summary>
    public (int Width, int Height) ExpectedSheetSize => (FrameWidth * Cols, FrameHeight * Rows);

    /// <summary>
    /// 这块画布在界面上该显示成多高（设计像素）。
    ///
    /// 换素材最容易出的问题就是"换成精灵图之后鲸鱼变小了/被裁了"：
    /// 静态形象是 168px 的框显示 610 画布（角色占 600），
    /// 现在画布变成 720 而角色还是 600，如果框还留 168，角色就只剩 140 了 ——
    /// 凭空小了 15%。
    ///
    /// 所以框必须跟着画布一起放大：
    ///     框高 = 角色的设计屏幕高度 × 帧高 ÷ 角色像素高
    /// 对样例（帧高 720、角色 600、设计高度 165）正好是 198，
    /// 屏幕上角色还是 165px，和静态形象严丝合缝。
    ///
    /// 帧表没写 characterHeight 时返回 null —— 由调用方决定退回哪种策略。
    /// </summary>
    public double? DisplayBoxHeight(double designCharacterHeight)
    {
        if (!HasCharacterHeight) return null;
        return designCharacterHeight * FrameHeight / CharacterHeight;
    }

    // ==================================================================
    // JSON 取值小工具（缺字段/类型不对都退回默认值，不抛）
    // ==================================================================

    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return "";
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : "";
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), out var parsed)) return parsed;   // 手写时写成 "8" 也认
        return fallback;
    }

    private static double ReadDouble(JsonElement obj, string name, double fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), out var parsed)) return parsed;
        return fallback;
    }

    private static bool ReadBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var v) && v,
            _ => false,
        };
    }
}

/// <summary>帧表解析的结果：要么给出一份可用的表，要么给出一句人能看懂的原因。</summary>
public sealed class AnimSheetParseResult
{
    public AnimSheet? Sheet { get; private init; }
    public string Error { get; private init; } = "";

    /// <summary>不影响播放、但值得让做素材的人知道的问题。</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    public bool Ok => Sheet != null;

    internal static AnimSheetParseResult Success(AnimSheet sheet, List<string> warnings) => new()
    {
        Sheet = sheet,
        Warnings = warnings,
    };

    internal static AnimSheetParseResult Failure(string error) => new() { Error = error };
}

/// <summary>
/// 悬浮窗认得的动作名，以及写法的归一化。
///
/// 动作键 = 素材目录名。目录名是人手敲的，大小写、下划线、连字符都会飘，
/// 所以统一归一化之后再比对 —— 否则用户把目录建成「Click_Bounce」就"没反应"，
/// 而界面上完全看不出为什么。
/// </summary>
public static class AnimActions
{
    /// <summary>单击小鲸鱼。</summary>
    public const string Click = "clickBounce";

    /// <summary>闲置时随机眨眼。</summary>
    public const string Blink = "blink";

    /// <summary>到点提醒。</summary>
    public const string Alert = "alert";

    /// <summary>界面会主动尝试播放的动作（顺序即优先级，仅用于诊断报告）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Click, Blink, Alert };

    /// <summary>
    /// 把目录名归一化成规范动作键。
    /// 认不出来的一律原样返回（带前后空格去掉）—— 允许用户自定义动作，
    /// 只是界面不会主动去播它。
    /// </summary>
    public static string Canonical(string? key)
    {
        var raw = (key ?? "").Trim();
        var k = raw.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

        return k switch
        {
            "click" or "clickbounce" or "tap" or "poke" => Click,
            "blink" or "idle" or "idleblink" or "blinkidle" => Blink,
            "alert" or "remind" or "reminder" or "notify" or "warning" => Alert,
            _ => raw,
        };
    }

    /// <summary>这个动作在帧表里找不找得到（键已归一化）。</summary>
    public static bool IsKnown(string canonical) => All.Contains(canonical, StringComparer.Ordinal);
}
