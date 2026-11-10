namespace Alexandra.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string InstanceName = "Local\\Alexandra.Voice.SingleInstance";
    private readonly Mutex _mutex = new(false, InstanceName);
    private readonly EventWaitHandle _activation = new(false, EventResetMode.AutoReset, "Local\\Alexandra.Voice.Activate");
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    public bool IsPrimary { get; }
    public static bool IsRunning => IsHeld(InstanceName);
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
    public SingleInstanceCoordinator()
    {
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }
    public void SignalPrimary() => _activation.Set();
    public void Listen(Action activate)
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(() =>
        {
            var handles = new[] { _activation, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            { if (_stop.IsCancellationRequested) return; activate(); }
        });
    }
    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Wait(TimeSpan.FromSeconds(1));
        _activation.Dispose(); _stop.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
