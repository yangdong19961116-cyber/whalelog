namespace WhaleLog.Models;

public enum AppThemeMode
{
    /// <summary>跟随 Windows 深/浅色设置。</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// 用户偏好。字段带默认值，配置文件缺字段或损坏时都能退回可用状态。
/// </summary>
public sealed class AppSettings
{
    // ---- 记录 ----
    public string UserName { get; set; } = "";

    // ---- 上下班与提醒 ----
    public int WorkStartHour { get; set; } = 9;
    public int WorkStartMinute { get; set; } = 0;
    public int WorkEndHour { get; set; } = 18;
    public int WorkEndMinute { get; set; } = 0;

    /// <summary>下班前多少分钟开始提醒。</summary>
    public int RemindMinutesBefore { get; set; } = 15;

    public bool ReminderEnabled { get; set; } = true;
    public bool ReminderSoundEnabled { get; set; } = true;
    public bool ReminderTrayBalloonEnabled { get; set; } = true;

    /// <summary>下班时间已过、且当天仍无记录时，每隔多少分钟再催一次（0 = 不再催）。</summary>
    public int OvertimeNudgeMinutes { get; set; } = 30;

    /// <summary>提醒暂停到的时间点（点「稍后提醒」时写入）。</summary>
    public DateTime? SnoozeUntil { get; set; }

    // ---- 快速记录 ----
    /// <summary>启用全局快捷键，随时呼出"记一笔"输入框。</summary>
    public bool QuickCaptureEnabled { get; set; } = true;

    /// <summary>呼出快速记录的组合键，形如 "Ctrl+Alt+N"。</summary>
    public string QuickCaptureHotkey { get; set; } = "Ctrl+Alt+N";

    // ---- 悬浮窗 ----
    public bool FloatingEnabled { get; set; } = true;

    /// <summary>
    /// 悬浮窗大小 / 透明度的取值范围。
    ///
    /// 定义在这里，界面滑杆的 Minimum/Maximum 和设置校验共用同一组常量 ——
    /// 两边各写一份的话，手改过 settings.json 的值会出现"滑杆显示一个数、实际用另一个数"。
    /// </summary>
    public const double MinFloatingScale = 0.7;
    public const double MaxFloatingScale = 1.6;
    public const double MinFloatingOpacity = 0.3;
    public const double MaxFloatingOpacity = 1.0;
    public double FloatingLeft { get; set; } = double.NaN;
    public double FloatingTop { get; set; } = double.NaN;
    public double FloatingScale { get; set; } = 1.0;
    public double FloatingOpacity { get; set; } = 1.0;
    public bool FloatingSnapToEdge { get; set; } = true;
    public bool FloatingAutoCollapse { get; set; } = true;

    /// <summary>
    /// 小鲸鱼播不播动态素材（素材目录 anim\ 下的精灵图动画）。
    ///
    /// 默认开。留这个开关是因为动态素材是用户可以自己做的：画得不好、
    /// 抖得厉害、或者干脆不喜欢动，都要有一条不用删文件的退路。
    /// 关掉之后行为和以前完全一样（静态形象 + 到点上下弹一下）。
    /// </summary>
    public bool FloatingAnimations { get; set; } = true;

    /// <summary>
    /// 桌面小鲸鱼用哪套形象（见 <c>CharacterPacks</c>）。
    ///
    /// 空串表示"没选过" —— 解析时按「新版溟月优先、素材不在就回落到原版鲸鱼娘」处理，
    /// 所以从旧版本升级上来的用户（配置里没这个字段）也能直接拿到新形象。
    /// 原版永远可用（它的形象图是编译进程序集的嵌入资源），因此这个值再怎么填
    /// 都不会解析成一个用不了的形象。
    /// </summary>
    public string FloatingCharacter { get; set; } = "";

    // ---- 文件归档 ----
    /// <summary>
    /// true = 额外把每个版本复制一份到 archive\ 目录，版本可回滚（占磁盘）。
    /// false = 只记录路径 + 哈希指纹，绝不复制、绝不移动原文件。默认 false。
    /// </summary>
    public bool SnapshotMode { get; set; }

    /// <summary>超过该大小（MB）的文件改用分块采样哈希。</summary>
    public int FullHashLimitMb { get; set; } = 200;

    public bool AutoOpenMainWindowOnDrop { get; set; }

    // ---- 系统集成 ----
    public bool StartWithWindows { get; set; }
    public AppThemeMode ThemeOverride { get; set; } = AppThemeMode.System;
    public bool MinimizeToTray { get; set; } = true;
    public bool HideMainWindowOnClose { get; set; } = true;
}
