using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Alexandra.Core;
using Microsoft.Win32.SafeHandles;

namespace Alexandra.Infrastructure;

public sealed class RawInputControllerSource : IControllerInputSource, IDisposable
{
    private const int WmInputDeviceChange = 0x00FE;
    private const int WmInput = 0x00FF;
    private const nuint GidcRemoval = 2;
    private const uint RidInput = 0x10000003;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RimTypeHid = 2;
    private const int HidpStatusSuccess = 0x00110000;
    private const ushort GenericDesktopPage = 0x01;

    private static readonly ushort[] ControllerUsages = [0x04, 0x05, 0x08];
    private readonly Dictionary<IntPtr, DeviceContext> _contexts = [];
    private IntPtr _targetWindow;
    private bool _started;
    private bool _disposed;

    public event EventHandler<ControllerButtonEvent>? ButtonChanged;
    public event EventHandler<string>? DeviceDisconnected;
    public event EventHandler? DevicesChanged;
    public event EventHandler<string>? InputError;

    public IReadOnlyList<ControllerDevice> Devices =>
        _contexts.Values
            .Select(context => new ControllerDevice(context.Id, context.Name, true))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Start(IntPtr targetWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("HOTAS Raw Input is available on Windows only.");
        if (targetWindow == IntPtr.Zero)
            throw new ArgumentException("A valid window handle is required.", nameof(targetWindow));

        var devices = ControllerUsages
            .Select(usage => new RawInputDevice
            {
                UsagePage = GenericDesktopPage,
                Usage = usage,
                Flags = RidevInputSink | RidevDevNotify,
                TargetWindow = targetWindow
            })
            .ToArray();
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new InvalidOperationException(
                $"Windows could not register HOTAS Raw Input (error {Marshal.GetLastWin32Error()}).");

        _targetWindow = targetWindow;
        _started = true;
    }

    public void ProcessWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (!_started || _disposed) return;
        try
        {
            if (message == WmInput)
                ProcessRawInput(lParam);
            else if (message == WmInputDeviceChange && (nuint)wParam == GidcRemoval)
                RemoveDevice(lParam);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InputError?.Invoke(this, ex.Message);
        }
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(rawInputHandle, RidInput, null, ref size, headerSize) == uint.MaxValue || size == 0)
            throw new InvalidOperationException(
                $"Windows could not size HOTAS input (error {Marshal.GetLastWin32Error()}).");

        var buffer = new byte[size];
        if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
            throw new InvalidOperationException(
                $"Windows could not read HOTAS input (error {Marshal.GetLastWin32Error()}).");

        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var pointer = pinned.AddrOfPinnedObject();
            var header = Marshal.PtrToStructure<RawInputHeader>(pointer);
            if (header.Type != RimTypeHid) return;

            var context = GetOrCreateContext(header.Device);
            if (context is null) return;

            var dataOffset = Marshal.SizeOf<RawInputHeader>();
            var reportSize = Marshal.ReadInt32(pointer, dataOffset);
            var reportCount = Marshal.ReadInt32(pointer, dataOffset + sizeof(int));
            if (reportSize <= 0 || reportCount <= 0) return;

            var reportOffset = dataOffset + (sizeof(int) * 2);
            for (var index = 0; index < reportCount; index++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(IntPtr.Add(pointer, reportOffset + (index * reportSize)), report, 0, reportSize);
                foreach (var change in context.ReadButtonChanges(report))
                    ButtonChanged?.Invoke(this, change);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private DeviceContext? GetOrCreateContext(IntPtr deviceHandle)
    {
        if (_contexts.TryGetValue(deviceHandle, out var existing)) return existing;
        var created = DeviceContext.TryCreate(deviceHandle);
        if (created is null) return null;
        _contexts.Add(deviceHandle, created);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    private void RemoveDevice(IntPtr deviceHandle)
    {
        if (!_contexts.Remove(deviceHandle, out var context)) return;
        context.Dispose();
        DeviceDisconnected?.Invoke(this, context.Id);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started && OperatingSystem.IsWindows())
        {
            var removals = ControllerUsages
                .Select(usage => new RawInputDevice
                {
                    UsagePage = GenericDesktopPage,
                    Usage = usage,
                    Flags = RidevRemove,
                    TargetWindow = IntPtr.Zero
                })
                .ToArray();
            _ = RegisterRawInputDevices(
                removals,
                (uint)removals.Length,
                (uint)Marshal.SizeOf<RawInputDevice>());
        }

        foreach (var context in _contexts.Values) context.Dispose();
        _contexts.Clear();
        _targetWindow = IntPtr.Zero;
        _started = false;
    }

    private sealed class DeviceContext : IDisposable
    {
        private readonly IntPtr _preparsedData;
        private readonly ButtonDefinition[] _buttons;
        private readonly HashSet<ButtonKey> _pressed = [];
        private bool _disposed;

        private DeviceContext(
            string id,
            string name,
            IntPtr preparsedData,
            ButtonDefinition[] buttons)
        {
            Id = id;
            Name = name;
            _preparsedData = preparsedData;
            _buttons = buttons;
        }

        public string Id { get; }
        public string Name { get; }

        public static DeviceContext? TryCreate(IntPtr deviceHandle)
        {
            var deviceName = GetDeviceName(deviceHandle);
            if (string.IsNullOrWhiteSpace(deviceName)) return null;

            uint preparsedSize = 0;
            _ = GetRawInputDeviceInfo(deviceHandle, RidiPreparsedData, IntPtr.Zero, ref preparsedSize);
            if (preparsedSize == 0) return null;

            var preparsed = Marshal.AllocHGlobal(checked((int)preparsedSize));
            try
            {
                if (GetRawInputDeviceInfo(deviceHandle, RidiPreparsedData, preparsed, ref preparsedSize) == uint.MaxValue)
                    return null;
                if (HidPGetCaps(preparsed, out var caps) != HidpStatusSuccess ||
                    caps.UsagePage != GenericDesktopPage ||
                    !ControllerUsages.Contains(caps.Usage) ||
                    caps.NumberInputButtonCaps == 0)
                    return null;

                var nativeCaps = new HidpButtonCaps[caps.NumberInputButtonCaps];
                var capCount = caps.NumberInputButtonCaps;
                if (HidPGetButtonCaps(0, nativeCaps, ref capCount, preparsed) != HidpStatusSuccess)
                    return null;

                var definitions = new List<ButtonDefinition>();
                var seen = new HashSet<ButtonKey>();
                foreach (var cap in nativeCaps.Take(capCount))
                {
                    var minimum = cap.IsRange != 0 ? cap.UsageMinOrUsage : cap.UsageMinOrUsage;
                    var maximum = cap.IsRange != 0 ? cap.UsageMaxOrReserved : cap.UsageMinOrUsage;
                    if (maximum < minimum || maximum - minimum > 512) continue;
                    for (var usage = minimum; usage <= maximum; usage++)
                    {
                        var key = new ButtonKey(cap.ReportId, cap.UsagePage, cap.LinkCollection, usage);
                        if (seen.Add(key))
                            definitions.Add(new ButtonDefinition(key, definitions.Count + 1));
                        if (usage == ushort.MaxValue) break;
                    }
                }

                if (definitions.Count == 0) return null;
                var friendlyName = GetFriendlyName(deviceName);
                var result = new DeviceContext(deviceName, friendlyName, preparsed, definitions.ToArray());
                preparsed = IntPtr.Zero;
                return result;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
            }
        }

        public IEnumerable<ControllerButtonEvent> ReadButtonChanges(byte[] report)
        {
            if (_disposed || report.Length == 0) yield break;
            var usesReportIds = _buttons.Any(button => button.Key.ReportId != 0);
            var reportId = usesReportIds ? report[0] : (byte)0;
            var relevant = _buttons
                .Where(button => !usesReportIds || button.Key.ReportId == reportId)
                .ToArray();
            if (relevant.Length == 0) yield break;

            var current = new HashSet<ButtonKey>();
            foreach (var group in relevant.GroupBy(button => (button.Key.UsagePage, button.Key.LinkCollection)))
            {
                var usages = new ushort[group.Count()];
                uint usageCount = (uint)usages.Length;
                var status = HidPGetUsages(
                    0,
                    group.Key.UsagePage,
                    group.Key.LinkCollection,
                    usages,
                    ref usageCount,
                    _preparsedData,
                    report,
                    (uint)report.Length);
                if (status != HidpStatusSuccess) continue;

                var byUsage = group.ToDictionary(button => button.Key.Usage);
                foreach (var usage in usages.Take(checked((int)usageCount)))
                    if (byUsage.TryGetValue(usage, out var button))
                        current.Add(button.Key);
            }

            var timestamp = DateTimeOffset.UtcNow;
            foreach (var button in relevant)
            {
                var isDown = current.Contains(button.Key);
                var wasDown = _pressed.Contains(button.Key);
                if (isDown == wasDown) continue;
                if (isDown) _pressed.Add(button.Key);
                else _pressed.Remove(button.Key);
                yield return new ControllerButtonEvent(Id, button.Index, isDown, timestamp);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Marshal.FreeHGlobal(_preparsedData);
            _pressed.Clear();
        }
    }

    private static string? GetDeviceName(IntPtr deviceHandle)
    {
        uint characterCount = 0;
        _ = GetRawInputDeviceInfo(deviceHandle, RidiDeviceName, IntPtr.Zero, ref characterCount);
        if (characterCount == 0) return null;
        var builder = new StringBuilder(checked((int)characterCount + 1));
        return GetRawInputDeviceInfo(deviceHandle, RidiDeviceName, builder, ref characterCount) == uint.MaxValue
            ? null
            : builder.ToString();
    }

    private static string GetFriendlyName(string devicePath)
    {
        using var handle = CreateFile(
            devicePath,
            0,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                if (HidDGetProductString(handle, buffer, 512))
                {
                    var product = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrWhiteSpace(product)) return product.Trim();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        var match = Regex.Match(devicePath, @"VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase);
        return match.Success
            ? $"HOTAS VID_{match.Groups["vid"].Value} PID_{match.Groups["pid"].Value}"
            : "Windows HID controller";
    }

    private readonly record struct ButtonKey(
        byte ReportId,
        ushort UsagePage,
        ushort LinkCollection,
        ushort Usage);

    private sealed record ButtonDefinition(ButtonKey Key, int Index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidpCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(46)] public ushort NumberInputButtonCaps;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidpButtonCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMinOrUsage;
        [FieldOffset(58)] public ushort UsageMaxOrReserved;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        [Out] byte[]? data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device,
        uint command,
        IntPtr data,
        ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device,
        uint command,
        StringBuilder data,
        ref uint size);

    [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    private static extern int HidPGetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", EntryPoint = "HidP_GetButtonCaps")]
    private static extern int HidPGetButtonCaps(
        int reportType,
        [Out] HidpButtonCaps[] buttonCaps,
        ref ushort buttonCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll", EntryPoint = "HidP_GetUsages")]
    private static extern int HidPGetUsages(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        [Out] ushort[] usageList,
        ref uint usageLength,
        IntPtr preparsedData,
        [In] byte[] report,
        uint reportLength);

    [DllImport("hid.dll", EntryPoint = "HidD_GetProductString")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidDGetProductString(
        SafeFileHandle hidDeviceObject,
        IntPtr buffer,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
