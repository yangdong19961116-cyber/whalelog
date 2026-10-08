using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 归档文件的按关键字筛选。
///
/// 单独拎出来是因为它必须**和界面无关**：界面那侧只能证明"打字之后列表变短了"，
/// 证明不了"该留下来的都留下来了"。真正的规则放在这里，自测能直接拿造好的数据断言。
///
/// 匹配字段刻意写死成几个（文件名 / 类别 / 路径 / 备注 / 命中规则），
/// 而不是反射遍历所有属性 —— 反射会把 Id、哈希这些用户根本看不见的东西也搜进来，
/// 出现"打了个 h 就命中一堆文件"这种莫名其妙的局面。
/// </summary>
public static class FileSearch
{
    /// <summary>关键字是否命中这个文件。空关键字算全部命中。</summary>
    public static bool Matches(FileRecord file, string? keyword)
    {
        var kw = Normalize(keyword);
        if (kw.Length == 0) return true;

        return Hit(file.FileName, kw)
            || Hit(file.Category, kw)
            || Hit(file.FullPath, kw)
            || Hit(file.Note, kw)
            || Hit(file.MatchedRule, kw);
    }

    /// <summary>按关键字筛出文件。空关键字原样返回（顺序不变）。</summary>
    public static List<FileRecord> Filter(IEnumerable<FileRecord> files, string? keyword)
    {
        var kw = Normalize(keyword);
        return kw.Length == 0
            ? files.ToList()
            : files.Where(f => Matches(f, kw)).ToList();
    }

    /// <summary>去掉首尾空白。全空白和"没输入"是同一件事，不留一个只吃掉空格的关键字。</summary>
    public static string Normalize(string? keyword) => (keyword ?? "").Trim();

    private static bool Hit(string? text, string keyword)
        => !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
