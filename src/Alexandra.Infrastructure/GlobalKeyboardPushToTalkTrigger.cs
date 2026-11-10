using System.Runtime.InteropServices;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class GlobalKeyboardPushToTalkTrigger : IPushToTalkTrigger, IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const uint KeyDown = 0x0100;
    private const uint KeyUp = 0x0101;
    private const uint SystemKeyDown = 0x0104;
    private const uint SystemKeyUp = 0x0105;
    private const uint Quit = 0x0012;
    private readonly int _virtualKey;
    private readonly string _threadName;
    private Thread? _thread;
    private HookProcedure? _hookProcedure;
    private IntPtr _hook;
    private uint _threadId;
    private bool _held;
    private TaskCompletionSource? _started;

    public GlobalKeyboardPushToTalkTrigger(int virtualKey, string? threadName = null)
    {
        _virtualKey = virtualKey;
        _threadName = threadName ?? "Alexandra keyboard PTT";
    }

    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Global keyboard push-to-talk is available on Windows only.");
        if (_thread is not null) return;
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = _threadName };
        _thread.Start();
        await _started.Task.WaitAsync(cancellationToken);
    }

    public Task StopAsync()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, Quit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
        _held = false;
        return Task.CompletedTask;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hookProcedure = HookCallback;
        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _hookProcedure, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
            _started?.TrySetException(
                new InvalidOperationException($"Keyboard hook failed: {Marshal.GetLastWin32Error()}"));
            return;
        }
        _started?.TrySetResult();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<KeyboardHook>(lParam);
            var message = unchecked((uint)wParam.ToInt64());
            if (input.VirtualKey == _virtualKey)
            {
                if ((message == KeyDown || message == SystemKeyDown) && !_held)
                {
                    _held = true;
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
                else if ((message == KeyUp || message == SystemKeyUp) && _held)
                {
                    _held = false;
                    Released?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => _ = StopAsync();

    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHook
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Value;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProcedure callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(
        IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(
        out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(
        uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
