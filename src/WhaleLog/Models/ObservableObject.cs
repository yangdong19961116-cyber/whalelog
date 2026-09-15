using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhaleLog.Models;

/// <summary>
/// 极简 INotifyPropertyChanged 基类。项目刻意不引入 MVVM 框架，
/// 这点绑定需求手写更省事。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
