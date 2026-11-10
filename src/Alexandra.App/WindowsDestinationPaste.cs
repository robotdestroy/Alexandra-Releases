using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Alexandra.Core;
using Alexandra.Infrastructure;

namespace Alexandra.App;

// The sole game-input exception: user-requested Ctrl+V into an already open foreground Galaxy Map.
// Never focuses windows, opens menus, selects all, presses Enter, plots a route or sends model text.
public sealed class WindowsDestinationPaste(Dispatcher dispatcher, Func<string> journalDirectory,
    Func<bool> isCurrent) : IDestinationPasteDriver
{
    public async Task<DestinationPasteResult> PasteAsync(CopiedDestination destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (dispatcher.HasShutdownStarted || !isCurrent()) throw new OperationCanceledException();
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var pid);
        if (window == IntPtr.Zero || pid == 0)
            return Blocked("elite_not_focused", "Put Elite in front and click the Galaxy Map search field, then say 'paste that'.");
        DateTimeOffset started;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            if (process.ProcessName != "EliteDangerous64" || process.MainWindowHandle != window || IsIconic(window))
                return Blocked("elite_not_focused", "Put Elite in front and click the Galaxy Map search field, then say 'paste that'.");
            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        { return Blocked("elite_unavailable", "I couldn't verify Elite's active window. No paste was sent."); }

        if (!MapReady(started))
            return Blocked("map_not_ready", "Open or reopen the Galaxy Map and click its search field, then say 'paste that'.");
        var clipboardMatches = await dispatcher.InvokeAsync(() => {
            token.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new OperationCanceledException();
            try
            {
                return GetClipboardSequenceNumber() == destination.ClipboardSequence &&
                    Clipboard.ContainsText(TextDataFormat.UnicodeText) &&
                    Clipboard.GetText(TextDataFormat.UnicodeText) == destination.System &&
                    GetClipboardSequenceNumber() == destination.ClipboardSequence;
            }
            catch (ExternalException) { return false; }
        });
        if (!clipboardMatches)
            return Blocked("clipboard_changed", "The clipboard changed or couldn't be verified. Ask me to copy the destination again.");
        if (OtherKeysDown(false))
            return Blocked("keys_held", "Release Control, Shift, Alt, Windows and V, then ask me to paste again.");

        var controlHeld = false;
        bool Safe() => !token.IsCancellationRequested && isCurrent() && GetForegroundWindow() == window &&
            !IsIconic(window) && GetClipboardSequenceNumber() == destination.ClipboardSequence &&
            MapReady(started) && !OtherKeysDown(controlHeld);
        var keys = new PasteKeys(Safe, value => controlHeld = value);
        var sent = await new FixedPasteGesture(keys, Safe).SendAsync(token);
        if (sent && GetClipboardSequenceNumber() != destination.ClipboardSequence)
            return new(true, destination.System, "paste_uncertain", "Paste sent, but the clipboard changed during it. Check the search field.");
        return sent
            ? new(true, destination.System, "paste_sent", "Paste sent.")
            : Blocked("input_not_sent", "The paste couldn't finish safely. Keep Elite's Galaxy Map search field selected and ask again.");
    }
    private bool MapReady(DateTimeOffset started)
    {
        try
        {
            var path = Path.Combine(journalDirectory(), "Status.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 65536) return false;
            using var reader = new StreamReader(stream);
            return GalaxyMapPasteEvidence.IsReady(reader.ReadToEnd(), started, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }
    private static bool OtherKeysDown(bool ownControl) =>
        new[] { 0x10, 0x12, 0x5B, 0x5C, 0xA3, 0x56 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0) ||
        !ownControl && (GetAsyncKeyState(0x11) & 0x8000) != 0;
    private static DestinationPasteResult Blocked(string status, string message) => DestinationPasteResult.Blocked(status, message);

    private sealed class PasteKeys(Func<bool> safe, Action<bool> controlState) : IFixedPasteKeys
    {
        // A fresh guard immediately before each key-down closes the async-check gap.
        public bool PressControl()
        {
            if (!safe() || !Send(0x11, false)) return false;
            controlState(true); return true;
        }
        public bool PressV() => safe() && Send(0x56, false);
        public void ReleaseV()
        {
            if (!Send(0x56, true) && !Send(0x56, true))
                throw new InvalidOperationException("Windows couldn't release the paste key. Tap and release V and Control.");
        }
        public void ReleaseControl()
        {
            try
            {
                if (!Send(0x11, true) && !Send(0x11, true))
                    throw new InvalidOperationException("Windows couldn't release the paste shortcut. Tap and release Control.");
            }
            finally { controlState(false); }
        }
    }
    private static bool Send(ushort key, bool up)
    {
        // Scancodes and short holds match the game-compatible technique used successfully in Dextra.
        var scan = (ushort)MapVirtualKey(key, 0);
        if (scan == 0) return false;
        var input = new Input { Type = 1, Data = new InputUnion {
            Keyboard = new KeyboardInput { Scan = scan, Flags = 0x0008u | (up ? 0x0002u : 0) }
        } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput {
        public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput {
        public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareInput { public uint Message; public ushort ParamL, ParamH; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
