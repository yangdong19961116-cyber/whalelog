using System.Windows.Threading;
using WhaleLog.Models;

namespace WhaleLog.Services;

public enum ReminderKind
{
    /// <summary>下班前提醒（默认提前 15 分钟）。</summary>
    BeforeOffWork = 0,
    /// <summary>到点了。</summary>
    OffWork = 1,
    /// <summary>已过下班时间、当天仍然没记录，反复催。</summary>
    OvertimeNudge = 2,
}

public sealed class ReminderEvent
{
    public ReminderKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>语气强度 0-2，悬浮窗据此决定动画幅度。</summary>
    public int Urgency { get; init; }
}

/// <summary>当天记录情况，提醒文案据此调整语气。</summary>
public sealed record DayStatus(int EntryCount, int OpenCount, int DoneCount, int FileCount)
{
    public bool IsEmpty => EntryCount == 0;
}

/// <summary>
/// 下班提醒调度。
///
/// 只负责"什么时候该提醒"，不负责"怎么提醒" —— 悬浮窗气泡、音效、托盘气泡
/// 都订阅 ReminderFired 自己决定。这样提醒渠道可以随时增删，不用改这个类。
/// </summary>
public sealed class ReminderService
{
    private readonly SettingsService _settings;
    private readonly Func<DayStatus> _statusProvider;

    private readonly DispatcherTimer _timer;
    private readonly HashSet<ReminderKind> _firedToday = new();
    private string _stateDate = DateTime.Today.ToString("yyyy-MM-dd");
    private DateTime? _lastNudgeAt;

    public ReminderService(SettingsService settings, Func<DayStatus> statusProvider)
    {
        _settings = settings;
        _statusProvider = statusProvider;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Tick();
    }

    public event Action<ReminderEvent>? ReminderFired;

    /// <summary>每次轮询都会触发，供悬浮窗刷新倒计时显示。</summary>
    public event Action? Ticked;

    public void Start()
    {
        _timer.Start();
        Tick();
    }

    public void Stop() => _timer.Stop();

    /// <summary>今天的下班时间点。</summary>
    public DateTime OffWorkTime
    {
        get
        {
            var s = _settings.Settings;
            return DateTime.Today.AddHours(s.WorkEndHour).AddMinutes(s.WorkEndMinute);
        }
    }

    public DateTime WorkStartTime
    {
        get
        {
            var s = _settings.Settings;
            return DateTime.Today.AddHours(s.WorkStartHour).AddMinutes(s.WorkStartMinute);
        }
    }

    /// <summary>距离下班还剩多久，已过点为负。</summary>
    public TimeSpan Remaining => OffWorkTime - DateTime.Now;

    public bool IsAfterWork => Remaining <= TimeSpan.Zero;

    public string RemainingText
    {
        get
        {
            var r = Remaining;
            if (r <= TimeSpan.Zero)
            {
                var over = -r;
                return over.TotalHours >= 1
                    ? $"已下班 {(int)over.TotalHours}h{over.Minutes:D2}m"
                    : $"已下班 {over.Minutes}m";
            }
            return r.TotalHours >= 1
                ? $"距下班 {(int)r.TotalHours}h{r.Minutes:D2}m"
                : $"距下班 {r.Minutes}m";
        }
    }

    /// <summary>点「稍后提醒」用：延后到指定分钟后再进入提醒窗口。</summary>
    public void Snooze(int minutes)
    {
        var until = DateTime.Now.AddMinutes(Math.Max(1, minutes));
        _settings.Update(s => s.SnoozeUntil = until);
        _firedToday.Remove(ReminderKind.BeforeOffWork);
        Tick();
    }

    public void ClearSnooze()
    {
        _settings.Update(s => s.SnoozeUntil = null);
    }

    /// <summary>手动重置今天的提醒状态（比如用户点「今天别再提醒」后想恢复）。</summary>
    public void ResetToday()
    {
        _firedToday.Clear();
        _lastNudgeAt = null;
        ClearSnooze();
        Tick();
    }

    private void Tick()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (today != _stateDate)
        {
            _stateDate = today;
            _firedToday.Clear();
            _lastNudgeAt = null;
        }

        Ticked?.Invoke();

        var s = _settings.Settings;
        if (!s.ReminderEnabled) return;
        if (s.SnoozeUntil.HasValue && s.SnoozeUntil.Value > DateTime.Now) return;

        var now = DateTime.Now;
        var end = OffWorkTime;
        var lead = TimeSpan.FromMinutes(Math.Max(0, s.RemindMinutesBefore));
        var preAt = end - lead;

        if (now >= preAt && now < end && _firedToday.Add(ReminderKind.BeforeOffWork))
        {
            var status = _statusProvider();
            ReminderFired?.Invoke(new ReminderEvent
            {
                Kind = ReminderKind.BeforeOffWork,
                Urgency = 1,
                Title = $"还有 {Math.Max(1, (int)Math.Ceiling((end - now).TotalMinutes))} 分钟下班",
                Message = BuildPreOffWorkMessage(status),
            });
            return;
        }

        if (now >= end && _firedToday.Add(ReminderKind.OffWork))
        {
            var status = _statusProvider();
            ReminderFired?.Invoke(new ReminderEvent
            {
                Kind = ReminderKind.OffWork,
                Urgency = 2,
                Title = "到点下班啦",
                Message = BuildOffWorkMessage(status),
            });
            return;
        }

        // 已经过了下班时间，当天还是一片空白 —— 按设定间隔反复催
        int nudge = s.OvertimeNudgeMinutes;
        if (nudge > 0 && now >= end)
        {
            var status = _statusProvider();
            bool shouldNudge = status.IsEmpty || status.OpenCount > 0;
            if (shouldNudge)
            {
                var last = _lastNudgeAt ?? end;
                if (now - last >= TimeSpan.FromMinutes(nudge))
                {
                    _lastNudgeAt = now;
                    ReminderFired?.Invoke(new ReminderEvent
                    {
                        Kind = ReminderKind.OvertimeNudge,
                        Urgency = status.IsEmpty ? 2 : 1,
                        Title = status.IsEmpty ? "今天一个字都还没记" : $"还有 {status.OpenCount} 条没收尾",
                        Message = status.IsEmpty
                            ? "先花一分钟记两笔吧，明天写日报就不用回忆了。"
                            : "把没做完的事标一下进度，明天接着干更顺。",
                    });
                }
            }
        }
    }

    private static string BuildPreOffWorkMessage(DayStatus status)
    {
        if (status.IsEmpty && status.FileCount == 0)
            return "今天还没开始记录，走之前先把做过的两三件事写下来？";

        if (status.OpenCount > 0)
            return $"已完成 {status.DoneCount} 条，还有 {status.OpenCount} 条没收尾，顺手标一下进度。";

        if (status.FileCount == 0)
            return "记录挺完整了。今天经手的文件要不要拖进来归个档？";

        return $"已完成 {status.DoneCount} 条，归档 {status.FileCount} 个文件。可以收工了。";
    }

    private static string BuildOffWorkMessage(DayStatus status)
    {
        if (status.IsEmpty && status.FileCount == 0)
            return "今天没有任何记录。写一行也算，别让它空着。";

        if (status.OpenCount > 0)
            return $"剩下 {status.OpenCount} 条未完成 —— 留给明天，还是今天收掉？";

        return $"今天 {status.DoneCount} 条记录、{status.FileCount} 个文件，收工。";
    }
}
