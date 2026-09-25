using KeySender.Models;

namespace KeySender.Input;

public interface IInputProvider
{
    Task TypeTextAsync(string text, InputTiming timing, CancellationToken cancellationToken);
    Task PressKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifiers, InputTiming timing, CancellationToken cancellationToken);
    void ReleaseModifiers();
}
