using System.IO;
using System.Text.Json;

namespace WhaleLog.Services;

/// <summary>
/// 一套形象素材的**画布几何**：源画布多大、角色在画布里占多高、四周留多少版式白。
///
/// 为什么需要这一层
/// ----------------
/// 悬浮窗要给"形象"一个显示框，而框该多大完全取决于素材：
///
///     框 = 角色在屏幕上的设计高度 × 画布尺寸 ÷ 角色在画布里的像素高
///
/// 角色在屏幕上的高度两套形象是**一样的**（165px）；变的只是画布 ——
///
///     原版（鲸鱼娘）  画布 610×610、角色 600 高  →  框 167.75×167.75
///     dsh（溟月）     画布 640×360、角色 270 高  →  框 391.1×220
///
/// 这两行的差别有多大，看 2026-09-20 用户报的故障就清楚了：
/// 当时没有这一层，框高是个写死的 168，**两套形象共用一把尺子**。
/// 同一把尺子量两套画布的结果是 —— dsh 的静态形象被显示成 116.8px（比它该有的小 29%），
/// 而一开动画又跳到 165px：**每播一次动作，形象放大一倍再缩回去**。
/// 用户的原话是"两套动画模板有时候会冲突"，指的就是这个。
///
/// 所以规矩改成：**每个形象包声明自己的画布几何，显示框各算各的**（"不要统一高度"）。
///
/// 几何从哪来
/// ----------
/// 形象包根目录 <c>index.json</c> 里的 <c>geometry</c> 段。
/// 没有这个文件时退回 <see cref="Classic"/> —— 那正是"框高 168"这套老数字的出处，
/// 所以原版、以及玩家自己丢进 characters\ 的形象包，行为一个像素都不会变。
///
/// 这个类刻意不碰 WPF，自测能直接编译它。
/// </summary>
public sealed class CharacterGeometry
{
    // ==================================================================
    // 版式基准（照着原版静态形象定的，改这两个数会动到所有人的窗口大小）
    // ==================================================================

    /// <summary>
    /// 角色在屏幕上的设计高度。**两套形象共用同一个数** ——
    /// 换形象时用户感觉不到"大小变了"，靠的就是它。
    /// </summary>
    public const double DesignCharacterHeight = 165;

    /// <summary>画布单边的上限。手写坏了的几何不该能把窗口撑到半个屏幕。</summary>
    public const int MaxCanvasSide = 4096;

    /// <summary>原版画布边长。</summary>
    public const int ClassicCanvasSide = 610;

    /// <summary>原版角色在画布里的像素高。</summary>
    public const int ClassicCharacterHeight = 600;

    /// <summary>
    /// 原版那个"框高 168"的真实值：165 × 610 ÷ 600。
    ///
    /// 老代码里它被写死成 168（四舍五入到整），差 0.25px。
    /// 现在改用精确值，这样 <see cref="ClassicSidePadding"/> 反推出来才是干净的 34.125，
    /// 加回去正好还是 236 —— 原版窗口尺寸一个像素都不动。
    /// </summary>
    public const double ClassicBoxSide = DesignCharacterHeight * ClassicCanvasSide / ClassicCharacterHeight;

    /// <summary>
    /// 展开态的基准窗口宽度。原版静态形象的框是 167.75，加上两侧的版式白正好是 236。
    /// </summary>
    public const double ExpandedWidth = 236;

    /// <summary>
    /// 展开态的基准窗口高度。= 原版静态形象的框 167.75 + 纵向留给气泡和状态条的 128.25。
    /// </summary>
    public const double ExpandedHeight = 296;

    /// <summary>纵向留给气泡和状态条的高度（两套形象一致 —— 那两行是版式，不是形象）。</summary>
    public static double VerticalPadding => ExpandedHeight - ClassicBoxSide;

    /// <summary>
    /// 原版静态形象两侧得到的版式白（34.125）。
    /// **只是算出来给诊断看的** —— 窗口不按它排版（见 <see cref="LayoutFor"/>）。
    /// </summary>
    public static double ClassicSidePadding => (ExpandedWidth - ClassicBoxSide) / 2;

    /// <summary>原版（鲸鱼娘）：610 画布、角色 600 高、悬浮差分和它同画布。</summary>
    public static readonly CharacterGeometry Classic =
        new(ClassicCanvasSide, ClassicCanvasSide, ClassicCharacterHeight, "内置默认");

    /// <summary>没有 index.json 时用的几何 —— 也就是原版。</summary>
    public static CharacterGeometry Default => Classic;

    // ------------------------------------------------------------------

    /// <summary>源画布宽（像素）。</summary>
    public int CanvasWidth { get; }

    /// <summary>源画布高（像素）。</summary>
    public int CanvasHeight { get; }

    /// <summary>角色在画布里的像素高。0 表示这份几何没写，用它算不出显示框。</summary>
    public int CharacterHeight { get; }

    /// <summary>锚点在画布里的位置（诊断用；渲染不依赖它 —— 整块画布一起显示）。</summary>
    public double AnchorX { get; }
    public double AnchorY { get; }

    /// <summary>这份几何是从哪读出来的（诊断报告里原样写出来）。</summary>
    public string Source { get; }

    private CharacterGeometry(
        int canvasWidth, int canvasHeight, int characterHeight,
        string source, double anchorX = 0, double anchorY = 0)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        CharacterHeight = characterHeight;
        Source = source;
        AnchorX = anchorX > 0 ? anchorX : canvasWidth / 2.0;
        AnchorY = anchorY > 0 ? anchorY : canvasHeight;
    }

    /// <summary>算不算一份能用的几何（三项都齐）。</summary>
    public bool IsValid => CanvasWidth > 0 && CanvasHeight > 0 && CharacterHeight > 0;

    /// <summary>
    /// 这块画布在界面上该显示成多大（设计像素）—— **宽和高各算各的**。
    ///
    /// 早期只有高度、宽度靠 Image 等比缩放从高度推出来，那等于假定画布是正方形。
    /// dsh 的视频是 16:9，硬塞进方形只能先 crop 成方的 —— 逐帧量过：
    /// 106 个动作里 25 个有内容被切（蝴蝶/烟花/气球这类带道具的最狠，左右各切掉 120px）。
    /// 用户看到的就是"新形象显示被裁掉"。
    /// </summary>
    public (double Width, double Height) DisplayBox(double designCharacterHeight = DesignCharacterHeight)
        => IsValid
            ? (designCharacterHeight * CanvasWidth / CharacterHeight,
               designCharacterHeight * CanvasHeight / CharacterHeight)
            : (0, 0);

    /// <summary>
    /// 整个展开态窗口该多大（设计像素）。
    ///
    /// 规则只有两条，都是"取大者"，于是**基准版式永远不会被改小**：
    ///
    ///     宽 = max(236, 显示框宽)          —— 装得下就用基准宽，装不下才跟着变宽
    ///     高 = max(296, 显示框高 + 128.25) —— 128.25 是气泡行 + 状态条行
    ///
    /// 这样原版的三种情形（静态 167.75、精灵图 198、以及**没有素材时的兜底**）
    /// 算出来都还是 236×296 / 236×326.25，和改动前一个像素不差；
    /// 而 dsh 的 391.1×220 要的宽度超过基准，窗口才会真的变宽。
    /// </summary>
    public (double Width, double Height) LayoutSize(double designCharacterHeight = DesignCharacterHeight)
    {
        var (w, h) = DisplayBox(designCharacterHeight);
        return LayoutFor(w, h);
    }

    /// <summary>
    /// 给定一个显示框，算出展开态窗口该多大。
    ///
    /// **排版规则只此一处**：窗口按形象包声明的几何算，但实际生效的显示框可能比它大
    /// （玩家自己塞了一套 720 画布的精灵图，而包里没有 index.json），
    /// 所以悬浮窗传进来的是"最终生效的那个框"。
    /// </summary>
    public static (double Width, double Height) LayoutFor(double boxWidth, double boxHeight)
        => (Math.Max(ExpandedWidth, boxWidth),
            Math.Max(ExpandedHeight, boxHeight + VerticalPadding));

    /// <summary>诊断报告里的一行。</summary>
    public string Describe()
    {
        if (!IsValid) return $"几何不完整（{Source}）—— 按原版显示";

        var (bw, bh) = DisplayBox();
        var (lw, lh) = LayoutSize();
        return $"画布 {CanvasWidth}×{CanvasHeight}、角色高 {CharacterHeight}px（{Source}）" +
               $"→ 显示框 {bw:0.#}×{bh:0.#}、窗口 {lw:0.#}×{lh:0.#}";
    }

    // ==================================================================
    // 读形象包
    // ==================================================================

    /// <summary>
    /// 读一个形象包根目录下的 <c>index.json</c>。
    ///
    /// **任何问题都退回 <see cref="Classic"/>**，绝不抛 —— 几何读不出来只该导致
    /// "显示得和原版一样大"，不该让悬浮窗起不来。老包的 <c>cell</c> 字段也认
    /// （当成方形画布），省得升级一次就得重新生成素材。
    /// </summary>
    public static CharacterGeometry Resolve(string? packRoot)
    {
        if (string.IsNullOrWhiteSpace(packRoot)) return Classic;

        try
        {
            var path = Path.Combine(packRoot, "index.json");
            if (!File.Exists(path)) return Classic;
            return Parse(File.ReadAllText(path), "index.json");
        }
        catch
        {
            return Classic;
        }
    }

    /// <summary>解析 index.json 的内容。随便错都退回原版几何，绝不抛。</summary>
    /// <param name="json">文件内容。</param>
    /// <param name="source">这份内容是哪来的（写进诊断报告）。</param>
    public static CharacterGeometry Parse(string json, string source = "index.json")
    {
        if (string.IsNullOrWhiteSpace(json)) return Classic;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Classic;
            if (!doc.RootElement.TryGetProperty("geometry", out var g) ||
                g.ValueKind != JsonValueKind.Object)
                return Classic;

            // 画布尺寸：新写法是 canvasWidth/canvasHeight，老写法是 cell（方形）
            int cw = ReadInt(g, "canvasWidth", 0);
            int ch = ReadInt(g, "canvasHeight", 0);
            if (cw <= 0 || ch <= 0)
            {
                int cell = ReadInt(g, "cell", 0);
                if (cell <= 0) return Classic;
                cw = ch = cell;
            }

            int charH = ReadInt(g, "characterHeight", 0);
            if (charH <= 0) return Classic;
            // 角色比画布还高说明这份几何是坏的 —— 按画布高处理（至少不会被裁）
            if (charH > ch) charH = ch;

            // 画布大得离谱的几何（手写坏了）按原版走，免得窗口被撑到半个屏幕
            if (cw > MaxCanvasSide || ch > MaxCanvasSide) return Classic;

            return new CharacterGeometry(
                cw, ch, charH, source,
                ReadDouble(g, "anchorX", 0),
                ReadDouble(g, "anchorY", 0));
        }
        catch
        {
            return Classic;
        }
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }

    private static double ReadDouble(JsonElement obj, string name, double fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }
}
