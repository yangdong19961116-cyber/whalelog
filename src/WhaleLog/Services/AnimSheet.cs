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

    /// <summary>容器类型。认 "sheet"（精灵图 PNG）与 "video"（透明 webm）。</summary>
    public string SheetType { get; private init; } = "sheet";

    /// <summary>精灵图文件名（相对帧表所在目录）。video 类型时为空。</summary>
    public string Sheet { get; private init; } = "";

    /// <summary>视频文件名（相对帧表所在目录）。sheet 类型时为空。</summary>
    public string Video { get; private init; } = "";

    /// <summary>
    /// 视频类型专用的 ffmpeg 滤镜串（把源画布裁进格子）。
    ///
    /// 为什么存在帧表里、而不是在代码里再算一遍：源坐标 ↔ 格子坐标的换算
    /// 只有一处实现（tools/convert_dsh_webm.py 的 Geometry），运行时原样复用。
    /// 两边各写一份的话，离线校验通过、运行时却错位，而且很难查。
    /// </summary>
    public string Filter { get; private init; } = "";

    /// <summary>视频动作（运行时用 ffmpeg 解码）。</summary>
    public bool IsVideo => SheetType.Equals("video", StringComparison.Ordinal);

    /// <summary>这个动作真正要读的媒体文件（精灵图或视频）。</summary>
    public string MediaFile => IsVideo ? Video : Sheet;

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
            if (!string.IsNullOrEmpty(type) &&
                !type.Equals("sheet", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("video", StringComparison.OrdinalIgnoreCase))
            {
                if (type.Equals("sequence", StringComparison.OrdinalIgnoreCase))
                    return AnimSheetParseResult.Failure(
                        "type=sequence（PNG 序列）暂不支持；静态图请用 type=sheet，" +
                        "透明视频请用 type=video");
                return AnimSheetParseResult.Failure(
                    $"不认识的 type：「{type}」，目前支持 \"sheet\"（精灵图）与 \"video\"（透明 webm）");
            }

            var isVideo = type.Equals("video", StringComparison.OrdinalIgnoreCase);

            // ---- 媒体文件名 ----
            // 精灵图和视频都只认文件名、不许带路径 —— 否则帧表能指到任意位置去
            var mediaField = isVideo ? "video" : "sheet";
            var mediaFile = ReadString(root, mediaField);
            if (string.IsNullOrWhiteSpace(mediaFile))
                return AnimSheetParseResult.Failure(
                    isVideo ? "缺字段 video（视频文件名）" : "缺字段 sheet（精灵图文件名）");
            if (mediaFile.Contains("..", StringComparison.Ordinal) ||
                mediaFile.IndexOfAny(new[] { '/', '\\' }) >= 0)
                return AnimSheetParseResult.Failure(
                    $"{mediaField} 只能是同目录下的文件名，不能带路径：「{mediaFile}」");

            // ---- 视频的裁剪滤镜 ----
            var filter = "";
            if (isVideo)
            {
                filter = ReadString(root, "filter");
                var filterError = ValidateFilter(filter);
                if (filterError.Length > 0) return AnimSheetParseResult.Failure(filterError);
            }

            // ---- 几何 ----
            int fw = ReadInt(root, "frameWidth", 0);
            int fh = ReadInt(root, "frameHeight", 0);
            if (fw <= 0 || fh <= 0)
                return AnimSheetParseResult.Failure($"帧尺寸不合法：{fw}×{fh}（frameWidth / frameHeight 必须是正整数）");
            if (fw > MaxFrameSide || fh > MaxFrameSide)
                return AnimSheetParseResult.Failure($"帧尺寸 {fw}×{fh} 超过上限 {MaxFrameSide}");

            int count = ReadInt(root, "count", 0);
            if (count <= 0 && !isVideo)
                return AnimSheetParseResult.Failure("缺字段 count（帧数），或者它不是一个正整数");
            if (count > MaxCount)
                return AnimSheetParseResult.Failure($"帧数 {count} 超过上限 {MaxCount}");

            // 网格只有精灵图用得上；视频没有"排帧"这回事，给 1×1 占位即可
            int cols = isVideo ? 1 : ReadInt(root, "cols", 0);
            int rows = isVideo ? Math.Max(1, count) : ReadInt(root, "rows", 0);
            if (cols <= 0 || rows <= 0)
                return AnimSheetParseResult.Failure($"网格不合法：{cols} 列 × {rows} 行（cols / rows 必须是正整数）");
            if (cols * rows < count)
                return AnimSheetParseResult.Failure(
                    $"网格放不下 {count} 帧：{cols} 列 × {rows} 行 = {cols * rows}");

            if (isVideo && count <= 0)
                warnings.Add("帧表没写 count（视频的帧数）—— 播放时以实际解出的帧数为准");

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
                Sheet = isVideo ? "" : mediaFile,
                Video = isVideo ? mediaFile : "",
                Filter = filter,
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
    /// 这块画布在界面上该显示成多大（设计像素）—— **宽和高各算各的**。
    ///
    /// 换素材最容易出的问题就是"换成精灵图之后鲸鱼变小了/被裁了"：
    /// 静态形象是 168px 的框显示 610 画布（角色占 600），
    /// 现在画布变成 720 而角色还是 600，如果框还留 168，角色就只剩 140 了 ——
    /// 凭空小了 15%。
    ///
    /// 所以框必须跟着画布一起放大：
    ///     框 = 角色的设计屏幕高度 × 帧尺寸 ÷ 角色像素高
    /// 对样例（720×720 帧、角色 600、设计高度 165）正好是 198×198，
    /// 屏幕上角色还是 165px，和静态形象严丝合缝。
    ///
    /// ## 为什么宽度也要算（2026-09-21）
    ///
    /// 原来只有高度、宽度靠 Image 的等比缩放从高度推出来 —— 那等于**假定画布是正方形**。
    /// dsh 那套视频素材的原画布是 16:9（640×360），角色只占宽度的 42%，
    /// 硬塞进正方形画布就必须先 `crop` 成方的，于是**道具和特效被裁**
    /// （实测 106 个动作里 25 个有内容被切，最狠的一个左右各切掉 120px）。
    ///
    /// 现在两轴都从帧表算：画布多宽，显示框就多宽，不再把非方形画布挤成方的。
    /// 帧表没写 characterHeight 时返回 null —— 由调用方决定退回哪种策略。
    /// </summary>
    public (double Width, double Height)? DisplayBox(double designCharacterHeight)
    {
        if (!HasCharacterHeight) return null;
        return (designCharacterHeight * FrameWidth / CharacterHeight,
                designCharacterHeight * FrameHeight / CharacterHeight);
    }

    /// <summary>只要高度时的便捷入口（诊断报告、老断言用）。</summary>
    public double? DisplayBoxHeight(double designCharacterHeight)
        => DisplayBox(designCharacterHeight)?.Height;

    // ==================================================================
    // 视频滤镜的校验
    // ==================================================================

    /// <summary>
    /// 校验帧表里的 ffmpeg 滤镜串。
    ///
    /// 这段字符串会被原样拼进 ffmpeg 命令，所以**必须当成不可信输入**：
    /// 帧表是用户可以手改的文件，放任它写任意内容就等于给了执行任意命令的口子
    /// （滤镜串里的 `,` 分隔下一个滤镜，写对了就能越出裁剪的范围）。
    ///
    /// 这里只放行"我们自己生成的形状"：
    ///
    ///     crop=宽:高:x:y
    ///     pad=宽:高:x:y:color=black@0
    ///     scale=宽:高:flags=lanczos
    ///
    /// 解析时**先按 `=` 切出滤镜名**（ffmpeg 是「名字=参数:参数」），再按 `:` 切参数 ——
    /// 反过来切会把 `crop=384:360:128:0` 的名字认成 `crop=384`，于是正常的滤镜串
    /// 全被判成"不认识的滤镜"。（这个坑真踩过：生成出来的 106 个动作一个都扫不进来。）
    ///
    /// 返回空串表示通过，否则返回人能看懂的原因。
    /// </summary>
    private static string ValidateFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return "";   // 允许不写，运行时会给默认值

        if (filter.Length > 512)
            return "filter 太长（超过 512 字符）—— 正常的裁剪滤镜不会这么长";

        foreach (var raw in filter.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0) return "filter 里有空的滤镜段（多写了逗号？）";

            // 「名字=参数」是标准写法；也容忍「名字:参数」这种老写法
            int eq = part.IndexOf('=');
            int colon = part.IndexOf(':');
            int cut = eq > 0 ? eq : colon;
            if (cut <= 0) return $"filter 段「{part}」不像是一个滤镜";

            var name = part[..cut];
            if (name is not ("crop" or "scale" or "pad"))
                return $"filter 只允许 crop / scale / pad，不接受「{name}」";

            foreach (var arg in part[(cut + 1)..].Split(':'))
            {
                if (arg.Length == 0) return $"filter 段「{part}」里有空参数";

                var inner = arg.IndexOf('=');
                if (inner > 0)
                {
                    var key = arg[..inner];
                    var value = arg[(inner + 1)..];

                    // 具名参数只放行两个：pad 的填充色、scale 的插值算法
                    if (key.Equals("color", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsAllowedPadColor(value))
                            return $"filter 段「{part}」的填充色只允许 black@0 / black / none，不接受「{value}」";
                        continue;
                    }

                    if (key.Equals("flags", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsAllowedScaleFlag(value))
                            return $"filter 段「{part}」的插值算法只允许 lanczos / bilinear / bicubic / neighbor，不接受「{value}」";
                        continue;
                    }

                    return $"filter 段「{part}」里不认识的参数名「{key}」";
                }

                // 位置参数：纯数字，或者白名单里的枚举词
                if (IsFilterNumber(arg)) continue;
                if (IsAllowedScaleFlag(arg)) continue;

                return $"filter 段「{part}」的参数「{arg}」含有不允许的字符";
            }
        }

        return "";
    }

    /// <summary>位置参数只允许数字（可带负号和小数点）。</summary>
    private static bool IsFilterNumber(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c) && c != '.' && c != '-') return false;
        }
        return true;
    }

    /// <summary>pad 的填充色：只要透明黑就是我们要的（黑边不透明会把角色框起来）。</summary>
    private static bool IsAllowedPadColor(string v)
        => v.Equals("black@0", StringComparison.OrdinalIgnoreCase)
        || v.Equals("black", StringComparison.OrdinalIgnoreCase)
        || v.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>scale 的插值算法白名单。默认是双三次，我们生成时用 lanczos。</summary>
    private static bool IsAllowedScaleFlag(string v)
        => v.Equals("lanczos", StringComparison.OrdinalIgnoreCase)
        || v.Equals("bilinear", StringComparison.OrdinalIgnoreCase)
        || v.Equals("bicubic", StringComparison.OrdinalIgnoreCase)
        || v.Equals("neighbor", StringComparison.OrdinalIgnoreCase)
        || v.Equals("fast_bilinear", StringComparison.OrdinalIgnoreCase)
        || v.Equals("spline", StringComparison.OrdinalIgnoreCase);

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
///
/// ## 动作名还兼职"角色"
///
/// 一个形象可以有很多个动作（dsh 那个形象有 106 个）。界面要能回答
/// "点击时该播哪些""闲下来该播什么"，就得知道每个动作是干什么用的。
/// 这件事用**目录名的前缀**表达，而不是另开一个字段：
///
///     idle-待机呼吸休闲      持续循环的待机
///     clickBounce-开心跃动   点击回应（多个时随机挑一个）
///     ambient-哈欠连天       闲置时随机冒出来的小动作
///
/// 前缀怎么认、认不出来怎么办，全在 <see cref="RoleOf"/> 一处。
/// 好处是**老素材不用改**：目录名叫 <c>blink</c> / <c>clickBounce</c> 的
/// 本来就在用，它们各自就是一个角色。
/// </summary>
public static class AnimActions
{
    /// <summary>单击小鲸鱼。</summary>
    public const string Click = "clickBounce";

    /// <summary>闲置时随机眨眼（一次性的小动作）。</summary>
    public const string Blink = "blink";

    /// <summary>到点提醒。</summary>
    public const string Alert = "alert";

    /// <summary>持续循环的待机动作。和 <see cref="Blink"/> 的区别是"一直播" vs "偶尔眨一下"。</summary>
    public const string Idle = "idle";

    /// <summary>被拖拽时循环播。</summary>
    public const string Drag = "drag";

    /// <summary>闲置时随机冒出来的小动作。</summary>
    public const string Ambient = "ambient";

    /// <summary>
    /// 成套的"演一段"：**按目录名顺序逐段播**，不是随机挑一个。
    ///
    /// 现在只有一个用场 —— 文件归档成功后演一段「清点归档 → 雀跃庆祝」。
    /// 和 <see cref="Ambient"/> 的区别正是"有序"：闲置小动作是各演各的，
    /// 而这一套有先后（先低头清点，再高兴地跳起来），顺序错了看着就不对。
    ///
    /// 因此同一个角色下的素材用 <c>1-</c>/<c>2-</c> 前缀定序
    /// （目录名即动作名，也是播放顺序的依据）：<c>celebrate-1-清点归档</c>、
    /// <c>celebrate-2-雀跃庆祝</c>。素材包里没有这个角色时，
    /// 归档就只是归档 —— 界面不该为了缺素材而报错（原版那套就没有）。
    /// </summary>
    public const string Celebrate = "celebrate";

    /// <summary>贴边滑动时播（当前没有自主走动，只在吸附滑行时用）。</summary>
    public const string Move = "move";

    /// <summary>转向。素材里有，但还没有用上它的交互。</summary>
    public const string Turn = "turn";

    /// <summary>所有角色。顺序即优先级，仅用于诊断报告。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Idle, Click, Blink, Ambient, Alert, Drag, Move, Turn, Celebrate,
    };

    /// <summary>这些角色是"一直循环"，播完不会自己结束。</summary>
    public static bool IsLoopingRole(string role)
        => role is Idle or Drag;

    /// <summary>
    /// 把目录名归一化成规范动作键。
    /// 认不出来的一律原样返回（带前后空格去掉）—— 允许用户自定义动作，
    /// 只是界面不会主动去播它。
    ///
    /// ⚠️ 注意 <c>"idle"</c> 仍然归一化成 <see cref="Blink"/>（历史行为，改了会让
    /// 老素材注册的名字变掉）。所以「目录叫 idle」的**动作键**是 blink，
    /// 但它的**角色**是 <see cref="Idle"/> —— 两者刻意不强行统一，见 <see cref="RoleOf"/>。
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

    /// <summary>
    /// 这个动作用来干什么。
    ///
    /// 只看目录名的**第一段**（第一个 <c>-</c> / <c>_</c> / 空格之前），
    /// 所以 <c>ambient-哈欠连天</c> 和 <c>ambient-偷吃零食</c> 都是
    /// <see cref="Ambient"/>，界面据此在两者之间随机挑。
    ///
    /// 认不出来返回空串 —— 那是用户自制的动作，界面不会主动播它，
    /// 但它的素材仍然被扫描、登记、能手动播。
    /// </summary>
    public static string RoleOf(string? key)
    {
        var raw = (key ?? "").Trim();
        if (raw.Length == 0) return "";

        var cut = raw.IndexOfAny(new[] { '-', '_', ' ' });
        var head = (cut < 0 ? raw : raw[..cut]).ToLowerInvariant();

        return head switch
        {
            "click" or "clickbounce" or "tap" or "poke" => Click,
            "blink" => Blink,
            "idle" or "idleloop" or "loop" => Idle,
            "alert" or "remind" or "reminder" or "notify" or "warning" => Alert,
            "drag" or "dragging" => Drag,
            "ambient" or "random" or "act" or "acts" => Ambient,
            "move" or "walk" => Move,
            "turn" => Turn,
            "celebrate" or "celebration" or "sequence" => Celebrate,
            _ => "",
        };
    }

    /// <summary>这个动作在帧表里找不找得到（键已归一化）。</summary>
    public static bool IsKnown(string canonical) => All.Contains(canonical, StringComparer.Ordinal);
}
