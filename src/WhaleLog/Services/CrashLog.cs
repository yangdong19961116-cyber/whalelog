using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 出错时往 logs\error.log 追加一条。
///
/// 刻意独立成一个小类（不依赖 WPF、不依赖 App）：设置、存储这些底层服务也要能记日志，
/// 而它们不能反过来依赖界面层 —— 也不该让存储服务在出问题时彻底静默。
/// </summary>
public static class CrashLog
{
    /// <summary>记一条带说明的错误。</summary>
    public static void Write(string what, Exception ex) => Append($"{what}：{ex}");

    /// <summary>记一个异常。</summary>
    public static void Write(Exception ex) => Append(ex.ToString());

    /// <summary>
    /// 记一条不带异常的信息。用于"出过问题但已兜住"的情况，
    /// 比如外部素材读不出来、自动回落成了内置图 —— 静默回落会让用户
    /// 以为自己那张图已经生效了，所以留一条痕迹。
    /// </summary>
    public static void Note(string what) => Append(what);

    private static void Append(string text)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志都写不了就放弃，不能因此再抛
        }
    }
}
