namespace WhaleLog.Services;

/// <summary>「自选播放动画」二级页里的一行的种类。</summary>
public enum AnimMenuRowKind
{
    /// <summary>分组标题（灰字、不可点）。</summary>
    Header,

    /// <summary>可点的动作。</summary>
    Action,
}

/// <summary>「自选播放动画」二级页里的一行。</summary>
/// <param name="Kind">这一行是标题还是动作。</param>
/// <param name="Text">显示文字（动作行已经去过角色前缀）。</param>
/// <param name="Action">动作行的动作键；标题行为 null。</param>
public readonly record struct AnimMenuRow(AnimMenuRowKind Kind, string Text, string? Action);

/// <summary>
/// 把素材库变成「自选播放动画」二级页的行清单。
///
/// **单独抽出来是为了能测**：菜单本身是代码构造出来的 WPF 控件，喂不进断言；
/// 而"列哪些行、叫什么名字、怎么分组、搜了关键词还剩什么"才是真正会写错的地方。
/// 这层是纯函数，自测能直接喂一份素材进去核对 —— 不用起窗口、不依赖形象包。
///
/// 现实约束（在真 dsh 包里数过）：106 个动作里 93 个是 ambient，名字还几乎全不重复
/// （"吃汤圆""写代码"…）—— 光靠按角色分组压不短，所以关键词一给就平铺。
/// </summary>
public static class AnimMenu
{
    /// <summary>
    /// 造出二级页的行清单。
    ///
    /// <paramref name="keyword"/> 为空 → 按角色分组（标题 + 组内动作）；
    /// 非空 → 平铺筛选结果（用户已经在缩小范围了，再分组只会碍事）。
    /// 筛不出东西时给一行提示，**不是空清单** —— 空清单会让二级页看着像坏了。
    /// </summary>
    public static IReadOnlyList<AnimMenuRow> Build(AnimLibrary lib, string? keyword)
    {
        var rows = new List<AnimMenuRow>();
        var hits = lib.Search(keyword);

        if (hits.Count == 0)
        {
            rows.Add(new AnimMenuRow(AnimMenuRowKind.Header, "没有匹配的动作", null));
            return rows;
        }

        if ((keyword ?? "").Trim().Length > 0)
        {
            foreach (var a in hits)
                rows.Add(new AnimMenuRow(AnimMenuRowKind.Action, AnimActions.ShortName(a, lib.RoleOf(a)), a));

            return rows;
        }

        foreach (var (role, actions) in lib.GroupedActions())
        {
            rows.Add(new AnimMenuRow(
                AnimMenuRowKind.Header,
                role.Length > 0
                    ? $"{AnimActions.DisplayName(role)}（{actions.Count}）"
                    : $"未分类（{actions.Count}）",
                null));

            foreach (var a in actions)
                rows.Add(new AnimMenuRow(AnimMenuRowKind.Action, AnimActions.ShortName(a, lib.RoleOf(a)), a));
        }

        return rows;
    }

    /// <summary>行清单里有多少个动作（自检核对"一个不少"用）。</summary>
    public static int ActionCount(IReadOnlyList<AnimMenuRow> rows)
        => rows.Count(r => r.Kind == AnimMenuRowKind.Action);
}
