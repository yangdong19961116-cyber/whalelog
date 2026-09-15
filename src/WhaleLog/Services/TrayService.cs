using System.Drawing;
using System.Windows.Forms;

namespace WhaleLog.Services;

/// <summary>
/// 系统托盘图标。
///
/// 只负责"图标 + 事件"，菜单由 App 用 WPF 的 ContextMenu 弹出 ——
/// WinForms 的 ContextMenuStrip 是经典灰白样式，在深色界面下会格格不入。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _ownedIcon;

    public TrayService()
    {
        _ownedIcon = IconAssets.LoadAppIcon();

        _icon = new NotifyIcon
        {
            Icon = _ownedIcon,
            Visible = true,
            Text = "鲸记 WhaleLog",
        };

        _icon.MouseUp += OnMouseUp;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>左键单击或双击。</summary>
    public event Action? OpenRequested;

    /// <summary>右键：请求在鼠标位置弹菜单。参数是屏幕坐标。</summary>
    public event Action<int, int>? MenuRequested;

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            OpenRequested?.Invoke();
        else if (e.Button == MouseButtons.Right)
            MenuRequested?.Invoke(Cursor.Position.X, Cursor.Position.Y);
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 63 字符，超了会抛异常
        if (string.IsNullOrEmpty(text)) text = "鲸记 WhaleLog";
        _icon.Text = text.Length > 63 ? text[..60] + "..." : text;
    }

    /// <summary>
    /// 重新加载托盘图标（用户在素材目录里换了 whalelog.ico 之后用）。
    /// 必须重新赋值 —— 只清缓存不赋值，托盘上还是旧图标。
    /// </summary>
    public void ReloadIcon()
    {
        try
        {
            _icon.Icon = IconAssets.LoadAppIcon();
        }
        catch
        {
            // 换图标失败就继续用旧的，不影响功能
        }
    }

    public void ShowBalloon(string title, string message, bool warning = false)
    {
        try
        {
            _icon.ShowBalloonTip(
                timeout: 8000,
                tipTitle: title,
                tipText: message,
                tipIcon: warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch
        {
            // 通知中心被禁用时忽略
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        // _ownedIcon 来自 GDI+ / 资源流，这里不做 DestroyIcon —— 它是进程内对象，
        // 退出时随进程回收，提前 Destroy 反而可能让残留的托盘图标变成空白方块。
    }
}
