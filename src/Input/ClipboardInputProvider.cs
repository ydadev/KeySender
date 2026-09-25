using System.Windows;
using KeySender.Models;
using KeySender.Native;

namespace KeySender.Input;

public sealed class ClipboardInputProvider(KeyboardInputProvider keyboard) : IInputProvider
{
    private const int ClipboardRestoreDelayMilliseconds = 100;
    public async Task TypeTextAsync(string text, InputTiming timing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDataObject? previous = null;
        bool previousClipboardRead = false;
        try { previous = Clipboard.GetDataObject(); previousClipboardRead = true; } catch { }
        try
        {
            Clipboard.SetText(text);
            await keyboard.PressKeyAsync(0x56, [0x11], timing, cancellationToken);
            await Task.Delay(ClipboardRestoreDelayMilliseconds, cancellationToken);
        }
        finally
        {
            try
            {
                if (previousClipboardRead)
                {
                    if (previous is not null) Clipboard.SetDataObject(previous, true);
                    else Clipboard.Clear();
                }
            }
            catch { }
            keyboard.ReleaseModifiers();
        }
    }

    public Task PressKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifiers, InputTiming timing, CancellationToken cancellationToken) =>
        keyboard.PressKeyAsync(virtualKey, modifiers, timing, cancellationToken);

    public void ReleaseModifiers() => keyboard.ReleaseModifiers();
}
