using System.Windows.Markup;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 在 XAML 里引用一个素材：<c>Source="{v:AssetImage whale-head}"</c>。
///
/// 不能直接写 pack:// URI —— 那样会绕过「外部素材优先」的解析，
/// 用户在 Assets\ 里换的图就不会生效。
/// </summary>
public sealed class AssetImageExtension : MarkupExtension
{
    public AssetImageExtension()
    {
    }

    public AssetImageExtension(string name) => Name = name;

    [ConstructorArgument("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// 素材加载失败时返回 null（图上什么都不显示），不影响布局 ——
    /// 引用它的 Image 都有自己的固定宽高。
    /// </summary>
    public override object? ProvideValue(IServiceProvider serviceProvider)
        => IconAssets.Load(Name);
}
