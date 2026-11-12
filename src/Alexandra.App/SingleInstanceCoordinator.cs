namespace Alexandra.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    internal const string DefaultName = "Local\\Alexandra.Voice";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly EventWaitHandle _restore;
    private readonly EventWaitHandle _ready;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public bool IsPrimary { get; }
    public static bool IsRunning => IsHeld(DefaultName + ".SingleInstance");
    public static bool WindowReady => IsRunning && IsReady(DefaultName);
    internal static bool IsReady(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name + ".Ready", out var ready)) return false;
        using (ready) return ready.WaitOne(0);
    }
    public static void RestorePrimary() => RestorePrimary(DefaultName);
    internal static void RestorePrimary(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name + ".Restore", out var restore))
            throw new InvalidOperationException("Alexandra has not exposed its window yet.");
        using (restore) restore.Set();
    }
    internal static bool IsHeld(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        using (mutex)
        {
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                return !acquired;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }
    public SingleInstanceCoordinator(string name = DefaultName)
    {
        _mutex = new(false, name + ".SingleInstance");
        _activation = new(false, EventResetMode.AutoReset, name + ".Activate");
        _restore = new(false, EventResetMode.AutoReset, name + ".Restore");
        _ready = new(false, EventResetMode.ManualReset, name + ".Ready");
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary) _ready.Reset();
    }
    public void SetReady(bool ready) { if (IsPrimary) { if (ready) _ready.Set(); else _ready.Reset(); } }
    public void SignalPrimary() => _activation.Set();
    public void Listen(Action activate, Action restore)
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(() =>
        {
            var handles = new[] { _activation, _restore, _stop.Token.WaitHandle };
            while (true)
            {
                var signal = WaitHandle.WaitAny(handles);
                if (_stop.IsCancellationRequested || signal == 2) return;
                if (signal == 0) activate(); else restore();
            }
        });
    }
    public void Dispose()
    {
        SetReady(false); _stop.Cancel();
        _listener?.Wait(TimeSpan.FromSeconds(1));
        _activation.Dispose(); _restore.Dispose(); _ready.Dispose(); _stop.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
