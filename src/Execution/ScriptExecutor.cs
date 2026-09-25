using KeySender.Input;
using KeySender.Models;

namespace KeySender.Execution;

public sealed class ScriptExecutor(IInputProvider keyboardProvider, IInputProvider clipboardProvider)
{
    public async Task ExecuteAsync(
        IReadOnlyList<ScriptCommand> commands,
        InputMode mode,
        InputTiming timing,
        int repeatCount,
        CancellationToken cancellationToken,
        Action<string> reportStatus)
    {
        IInputProvider textProvider = mode == InputMode.Clipboard ? clipboardProvider : keyboardProvider;
        int total = commands.Count * repeatCount;
        try
        {
            for (int repetition = 1; repetition <= repeatCount; repetition++)
            {
                for (int index = 0; index < commands.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    ScriptCommand command = commands[index];
                    reportStatus(repeatCount == 1
                        ? $"Команда {index + 1} из {commands.Count}: {Describe(command)}"
                        : $"Повтор {repetition}/{repeatCount}; команда {index + 1} из {commands.Count}: {Describe(command)}");

                    switch (command)
                    {
                        case TextCommand text:
                            await textProvider.TypeTextAsync(text.Text, timing, cancellationToken);
                            break;
                        case KeyCommand key:
                            await keyboardProvider.PressKeyAsync(key.VirtualKey, key.Modifiers, timing, cancellationToken);
                            if (key.VirtualKey == 0x0D && timing.EnterDelay > 0)
                                await Task.Delay(timing.EnterDelay, cancellationToken);
                            break;
                        case WaitCommand wait:
                            reportStatus($"WAIT {wait.Milliseconds} мс");
                            await Task.Delay(wait.Milliseconds, cancellationToken);
                            break;
                    }
                }
            }
            reportStatus($"Выполнено ({total} команд)");
        }
        finally
        {
            keyboardProvider.ReleaseModifiers();
            if (!ReferenceEquals(keyboardProvider, clipboardProvider)) clipboardProvider.ReleaseModifiers();
        }
    }

    private static string Describe(ScriptCommand command) => command switch
    {
        TextCommand text => $"Текст, строка {text.Line}",
        KeyCommand key => key.Modifiers.Count == 0 ? key.Name : $"{string.Join("+", key.Modifiers.Select(ModifierName))}+{key.Name}",
        WaitCommand wait => $"WAIT {wait.Milliseconds} мс",
        _ => "…"
    };

    private static string ModifierName(ushort key) => key switch
    {
        0x10 => "SHIFT", 0x11 => "CTRL", 0x12 => "ALT", 0x5B => "WIN", _ => "?"
    };
}
