using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 形象包：一整套「桌面小鲸鱼」的素材（静态本体 + 悬浮差分 + 一组动画）。
///
/// 为什么要引入这一层
/// ------------------
/// 原来全程序的形象素材都散在 <c>Assets\</c> 根下（whale.png / whale-hover.png /
/// anim\...），只够放**一套**形象。要同时留"新版溟月"和"原版鲸鱼娘"两套、
/// 还要能一键切换，就必须给每套形象一个自己的根目录。
///
/// 目录约定（<c>&lt;素材根&gt;\characters\&lt;形象ID&gt;\</c>）：
///
///     Assets\characters\dsh\
///         whale.png            静态本体（缺省时用 anim\base.png）
///         whale-hover.png      悬浮差分（可选）
///         anim\idle-xxx\       sheet.png + sheet.json
///         anim\clickBounce-xxx\
///         来源与许可.txt        许可要求署名，必须跟着素材走
///
/// **原版形象不搬家**：它的根目录就是 <c>Assets\</c> 本身，沿用老的平铺布局
/// （<c>Assets\whale.png</c>、<c>Assets\anim\</c>）。
/// 理由是老用户可能已经往那儿放过自己的图，为了新功能让人挪文件是不可接受的。
/// 代价是原版的根目录和别的包不在同一层 —— 但那只体现在 <see cref="DirOf"/> 一处。
///
/// 这个类刻意不碰 WPF，自测能直接编译它。
/// </summary>
public static class CharacterPacks
{
    /// <summary>新版形象（dsh-pet 的「溟月」），默认形象。</summary>
    public const string DshId = "dsh";

    /// <summary>原版形象（鲸鱼娘）。根目录就是素材根，不额外套一层。</summary>
    public const string ClassicId = "classic";

    /// <summary>放形象包的子目录名。</summary>
    public const string FolderName = "characters";

    /// <summary>一个形象包的描述。找不到目录时 <see cref="IsAvailable"/> 为 false。</summary>
    public sealed record Pack(string Id, string Title, string Root, string Note, bool IsAvailable);

    /// <summary>内置形象的友好名。认不出来的 ID 直接用 ID 当标题（允许用户自建形象包）。</summary>
    public static string TitleOf(string id) => id switch
    {
        DshId => "溟月 · 蓝鲸（新版）",
        ClassicId => "鲸鱼娘（原版）",
        _ => id,
    };

    /// <summary>界面上给这个形象的一句说明。</summary>
    public static string NoteOf(string id) => id switch
    {
        DshId => "dsh-pet 的透明动画形象：待机、点击回应、拖拽、95 个随机小动作。",
        ClassicId => "原来那只鲸鱼娘：静态形象 + 悬浮闭眼差分，动画要自己放素材。",
        _ => "外部形象包。",
    };

    /// <summary>
    /// 形象包的根目录。原版是素材根本身，其余的都在 <c>characters\&lt;id&gt;\</c> 下。
    ///
    /// 路径非法（ID 里带路径分隔符之类）时退回素材根 —— 宁可"打开的不是想要的那个"，
    /// 也不要因为一个手改的 ID 让程序起不来。
    /// </summary>
    public static string DirOf(string id, string? assetDir = null)
    {
        var root = assetDir ?? AppPaths.AssetsDir;

        if (string.IsNullOrWhiteSpace(id) || id.Equals(ClassicId, StringComparison.OrdinalIgnoreCase))
            return root;

        if (id.IndexOfAny(new[] { '/', '\\' }) >= 0 || id.Contains("..", StringComparison.Ordinal))
            return root;

        try { return Path.Combine(root, FolderName, id); }
        catch { return root; }
    }

    /// <summary>
    /// 这个形象能不能用。
    ///
    /// 原版永远算可用 —— 它的图是编译进程序集的嵌入资源，素材目录整个不存在也能显示。
    /// 其余的必须有目录，且目录里有 anim\ 或至少一张形象图，否则界面上选它是没有意义的。
    /// </summary>
    public static bool IsAvailable(string id, string? assetDir = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Equals(ClassicId, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            var dir = DirOf(id, assetDir);
            if (!Directory.Exists(dir)) return false;

            return Directory.Exists(Path.Combine(dir, "anim"))
                || File.Exists(Path.Combine(dir, "whale.png"))
                || File.Exists(Path.Combine(dir, "anim", "base.png"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 扫出可用的形象 ID。内置两个固定排在最前（新版优先），
    /// 再附上用户自己丢进 <c>characters\</c> 的其它形象包 —— 允许玩家自制形象，不用改代码。
    /// </summary>
    public static IReadOnlyList<string> Discover(string? assetDir = null)
    {
        var list = new List<string> { DshId, ClassicId };

        try
        {
            var root = Path.Combine(assetDir ?? AppPaths.AssetsDir, FolderName);
            if (!Directory.Exists(root)) return list;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (list.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(id);
            }
        }
        catch
        {
            // 扫不动就只给内置的：形象包不是程序能不能跑的前提
        }

        return list;
    }

    /// <summary>可用的形象包，附带各自是否就绪。设置界面用它列表。</summary>
    public static IReadOnlyList<Pack> Available(string? assetDir = null)
        => Discover(assetDir)
            .Select(id => new Pack(id, TitleOf(id), DirOf(id, assetDir), NoteOf(id),
                                   IsAvailable(id, assetDir)))
            .ToList();

    /// <summary>
    /// 把设置里存的值解析成一个**确实能用**的形象 ID。
    ///
    /// 降级顺序：设置里那个（若可用）→ dsh（若可用）→ 原版。
    /// 原版永远兜底，所以这个函数永远不会返回一个用不了的形象 ——
    /// 用户把素材删了、或者从旧版本升级上来（设置里没这个字段），界面都不会空。
    /// </summary>
    public static string Resolve(string? wanted, string? assetDir = null)
    {
        if (!string.IsNullOrWhiteSpace(wanted) && IsAvailable(wanted, assetDir))
            return wanted!;

        if (IsAvailable(DshId, assetDir)) return DshId;
        return ClassicId;
    }

    /// <summary>
    /// 这个形象是不是"动画素材另放"的那一类。目前所有形象都走 anim\ 约定，
    /// 留这个判断是为了让调用点读起来明确，而不是散着一个 true。
    /// </summary>
    public static bool UsesAnimFolder(string id) => !string.IsNullOrWhiteSpace(id);
}
