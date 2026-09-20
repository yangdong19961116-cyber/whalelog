using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 读写 settings.json。文件损坏或字段缺失时退回默认值，绝不因为配置问题启动失败。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 设置文件是给人看、给人改的，中文用户名不要转义成 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // FloatingLeft / FloatingTop 默认是 NaN（表示"还没保存过位置"），
        // 而 JSON 规范里没有 NaN 这个字面量 —— 不加这个开关，序列化会直接抛异常。
        // 因为 Save 是容错的（吞异常），表现就是"全新安装时设置怎么改都存不下去"，
        // 而且一点提示都没有。写成 "NaN" 字符串，读回来还是 NaN。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public AppSettings Settings { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            var path = AppPaths.SettingsFile;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null) Settings = s;
            }
        }
        catch
        {
            Settings = new AppSettings();
        }

        Migrate();
        Sanitize();
    }

    /// <summary>
    /// 设置文件的一次性迁移。**只在 <see cref="AppSettings.SettingsRevision"/> 落后时才动手**，
    /// 用户自己选过的值不动。
    ///
    /// 第 2 代（1.4.4）：`autoOpenMainWindowOnDrop` 在第 1 代是个**死字段** ——
    /// 没有任何代码读它，只写不读。所以磁盘上那个 `false` 不代表用户的任何选择，
    /// 只代表当年写进文件时的默认值。1.4.4 起它真的管事了（拖文件到小鲸鱼上要不要弹主面板），
    /// 而这个新功能必须默认能用 —— 否则用户拖完"什么都没发生"、还找不到原因
    /// （表现就是"新功能没生效"，而配置文件里那一行看着毫无异常）。
    ///
    /// 所以碰见第 1 代的文件就把这一项强制打开一次；此后用户怎么选都尊重。
    /// 这是**唯一**一处按代次覆盖用户文件的地方，加新迁移前先想清楚有没有别的办法。
    /// </summary>
    private void Migrate()
    {
        const int CurrentRevision = 2;

        var s = Settings;
        if (s.SettingsRevision >= CurrentRevision) return;

        s.AutoOpenMainWindowOnDrop = true;
        s.SettingsRevision = CurrentRevision;
    }

    /// <summary>把明显越界的值拉回合理区间，避免外部手改配置把程序搞崩。</summary>
    private void Sanitize()
    {
        var s = Settings;
        s.WorkStartHour = Math.Clamp(s.WorkStartHour, 0, 23);
        s.WorkStartMinute = Math.Clamp(s.WorkStartMinute, 0, 59);
        s.WorkEndHour = Math.Clamp(s.WorkEndHour, 0, 23);
        s.WorkEndMinute = Math.Clamp(s.WorkEndMinute, 0, 59);
        s.RemindMinutesBefore = Math.Clamp(s.RemindMinutesBefore, 0, 240);
        s.OvertimeNudgeMinutes = Math.Clamp(s.OvertimeNudgeMinutes, 0, 240);
        s.FloatingScale = Math.Clamp(s.FloatingScale, AppSettings.MinFloatingScale, AppSettings.MaxFloatingScale);
        s.FloatingOpacity = Math.Clamp(s.FloatingOpacity, AppSettings.MinFloatingOpacity, AppSettings.MaxFloatingOpacity);
        s.FullHashLimitMb = Math.Clamp(s.FullHashLimitMb, 1, 4096);
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(Settings, JsonOpts);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            // 尽力而为：写不进去也不影响本次会话使用。
            // 但**必须留下痕迹** —— 之前这里完全静默，结果"NaN 写不出去"这个
            // 问题藏了很久，表现只是"设置改了不生效"，从外部完全看不出原因。
            CrashLog.Write("保存设置失败", ex);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Settings);
        Sanitize();
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// 当前设置的快照（JSON）。
    ///
    /// 诊断自检会为了验证界面去改内存里的设置对象；程序退出时 <see cref="Save"/> 会把
    /// 这个对象整个写回文件，于是探针的改动就"泄漏"进用户配置了。
    /// 所以自检和截图这类入口在开始时留一份快照、结束时整体恢复。
    /// </summary>
    public string Snapshot() => JsonSerializer.Serialize(Settings, JsonOpts);

    /// <summary>把设置整体恢复成快照的样子（连对象一起换掉，漏改哪个字段都能兜住）。</summary>
    public void Restore(string snapshot)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(snapshot, JsonOpts);
            if (s != null)
            {
                Settings = s;
                Changed?.Invoke();
            }
        }
        catch
        {
            // 快照坏了就保持现状，总比把设置清空好
        }
    }
}
