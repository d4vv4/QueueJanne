namespace QueueJanne.App.ViewModels;

/// <summary>Turns a flag on for a moment so the view can play its animation. Starting again during a pulse restarts it.</summary>
internal sealed class Pulse(Action<bool> set)
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1600);

    private int _version;

    public async void Start()
    {
        var version = ++_version;
        set(false);

        // Let the view drop the class first, so the animation starts again.
        await Task.Yield();
        set(true);
        await Task.Delay(Duration);
        if (version == _version)
        {
            set(false);
        }
    }
}
