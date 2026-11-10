using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Alexandra.Core;

namespace Alexandra.App;

public sealed class WindowsSystemClipboard(Dispatcher dispatcher, Func<bool> isCurrent,
    CopiedDestinationMemory? destinations = null) : ISystemClipboard
{
    public async Task<bool> TryCopyAsync(string system, CancellationToken token)
    {
        if (!AnswerPolicy.IsSystemName(system)) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (dispatcher.HasShutdownStarted) throw new OperationCanceledException();
            var copied = await dispatcher.InvokeAsync(() => {
                token.ThrowIfCancellationRequested();
                // Run the turn check and write atomically on the UI thread, including on retries.
                if (!isCurrent()) throw new OperationCanceledException();
                try
                {
                    Clipboard.SetText(system, TextDataFormat.UnicodeText);
                    var sequence = GetClipboardSequenceNumber();
                    if (Clipboard.GetText(TextDataFormat.UnicodeText) != system || GetClipboardSequenceNumber() != sequence) return false;
                    destinations?.Remember(system, sequence);
                    return true;
                }
                catch (ExternalException) { return false; }
            });
            if (copied) return true;
            if (attempt < 2) await Task.Delay(150, token);
        }
        return false;
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
