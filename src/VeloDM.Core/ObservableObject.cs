using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VeloDM.Core;

/// <summary>
/// Minimal INotifyPropertyChanged base. The download engine mutates models from worker
/// threads, so a UI can install <see cref="Dispatcher"/> to marshal notifications onto
/// its own thread.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public static Action<Action>? Dispatcher { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (PropertyChanged is null) return;
        var args = new PropertyChangedEventArgs(name);
        if (Dispatcher is { } dispatch)
            dispatch(() => PropertyChanged?.Invoke(this, args));
        else
            PropertyChanged?.Invoke(this, args);
    }

    protected void Notify(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
