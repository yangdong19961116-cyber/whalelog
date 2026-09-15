namespace WhaleLog.Services;

/// <summary>
/// 命令行开关的解析。
///
/// 单独成类是为了能被自测直接编译 —— 这类字符串处理看着简单，但踩过坑：
/// `--export-assets --quiet` 里，取值逻辑会把后面那个开关当成前一个的值，
/// 于是素材被导出到一个名叫 `--quiet` 的目录里（真的发生过）。
/// 开关后面没带值、或者紧接着另一个开关，这两种情况界面上完全看不出来。
/// </summary>
public sealed class CommandLine
{
    private readonly string[] _args;

    public CommandLine(string[]? args) => _args = args ?? Array.Empty<string>();

    /// <summary>有没有这个开关。`--x` 和 `--x=值` 都算有。</summary>
    public bool Has(string flag)
        => _args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)
                       || a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取开关的值，支持 `--x 值` 和 `--x=值` 两种写法。
    /// 取不到（没写值，或后面紧跟的是另一个开关）时返回 null。
    /// </summary>
    public string? Value(string flag)
    {
        for (int i = 0; i < _args.Length; i++)
        {
            var arg = _args[i];

            if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(flag.Length + 1)..];

            if (!arg.Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;

            // 后面那个如果也是开关，就说明这个开关没带值 ——
            // 不判断的话 `--export-assets --quiet` 会把 "--quiet" 当目录名。
            if (i + 1 < _args.Length && !IsFlag(_args[i + 1]))
                return _args[i + 1];

            return null;
        }

        return null;
    }

    /// <summary>以 `-` 开头的一律当成开关，不当成值。</summary>
    private static bool IsFlag(string arg) => arg.StartsWith('-');
}
