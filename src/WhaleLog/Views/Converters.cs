using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WhaleLog.Views;

/// <summary>
/// 分类名 → 圆点颜色。
///
/// 刻意用固定的中饱和度调色板，而不是跟着主题走 —— 这些颜色只出现在小圆点上，
/// 深浅色背景下都能看清，省掉了给每个分类维护两套色的麻烦。
/// </summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // 笔记分类
        ["工作"] = "#546AAC",
        ["会议"] = "#B5754A",
        ["沟通"] = "#4E9A87",
        ["汇报"] = "#A85B8A",
        ["排查"] = "#C06A5A",
        ["学习"] = "#6E8B3D",
        ["其他"] = "#7A8290",

        // 文件分类
        ["汇报材料"] = "#A85B8A",
        ["合同协议"] = "#B5754A",
        ["会议材料"] = "#C08A3E",
        ["设计稿"] = "#8A6BB5",
        ["需求方案"] = "#546AAC",
        ["表格数据"] = "#3F8F6A",
        ["演示文稿"] = "#C06A5A",
        ["文档"] = "#5B7FA8",
        ["代码"] = "#4E8FA8",
        ["图片"] = "#7FA855",
        ["压缩包"] = "#9A7B4F",
        ["音视频"] = "#A8557F",
        ["安装包"] = "#6B7280",
        ["数据与日志"] = "#7A8290",
        ["未分类"] = "#8A919E",
    };

    private static readonly string[] Fallback =
    {
        "#546AAC", "#4E9A87", "#A85B8A", "#B5754A", "#6E8B3D", "#C06A5A",
    };

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "";
        if (key.Length == 0) key = "未分类";

        if (Cache.TryGetValue(key, out var cached)) return cached;

        var hex = Map.TryGetValue(key, out var h)
            ? h
            : Fallback[Math.Abs(key.GetHashCode()) % Fallback.Length];

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed，false → Visible。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>字符串非空 → Visible。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 日期分组的栏头文案。绑定源是分组键本身（yyyy-MM-dd），输出「今天 · 9月11日」这种写法。
/// </summary>
public sealed class DateGroupLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Services.DateGrouping.Label(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>文件已失效 → 用警示色显示状态。</summary>
public sealed class MissingToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool missing = value is true;
        var key = missing ? "WarningBrush" : "TextMutedBrush";
        return Application.Current?.TryFindResource(key) as Brush
               ?? (missing ? Brushes.Orange : Brushes.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
