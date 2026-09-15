using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace WhaleLog.Views;

/// <summary>
/// 日报 / 周报的查看窗。
///
/// 正文用只读 TextBox 而不是 TextBlock：生成出来就是要拿去粘贴的，
/// TextBox 天生支持 Ctrl+A / Ctrl+C 和拖选，省掉额外的选择逻辑。
/// </summary>
public sealed partial class ReportWindow : Window
{
    private string? _savedPath;

    public ReportWindow()
    {
        InitializeComponent();
    }

    public void Load(string title, string subtitle, string content, string? savedPath, string hint)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        Body.Text = content;
        _savedPath = savedPath;
        HintText.Text = hint;

        OpenFolderButton.Visibility = string.IsNullOrEmpty(savedPath)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* 鼠标状态异常时忽略 */ }
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(Body.Text))
            {
                Clipboard.SetText(Body.Text);
                HintText.Text = "已复制到剪贴板";
            }
        }
        catch
        {
            HintText.Text = "复制失败，剪贴板可能被其他程序占用";
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_savedPath)) return;

            if (File.Exists(_savedPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_savedPath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(_savedPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                    });
                }
            }
        }
        catch
        {
            HintText.Text = "打开目录失败";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
