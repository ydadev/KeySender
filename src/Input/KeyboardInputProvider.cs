using System.ComponentModel;
using System.Runtime.InteropServices;
using KeySender.Models;
using KeySender.Native;

namespace KeySender.Input;

public sealed class KeyboardInputProvider : IInputProvider
{
    private readonly HashSet<ushort> _heldModifiers = [];

    public async Task TypeTextAsync(string text, InputTiming timing, CancellationToken cancellationToken)
    {
        foreach (char character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character == '\n') continue;
            TypeCharacter(character);
            int delay = timing.NextCharacterDelay();
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            else await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async Task PressKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifiers, InputTiming timing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new List<Win32.Input>();
        foreach (ushort modifier in modifiers) { _heldModifiers.Add(modifier); inputs.Add(Key(modifier, false)); }
        inputs.Add(Key(virtualKey, false));
        inputs.Add(Key(virtualKey, true));
        for (int index = modifiers.Count - 1; index >= 0; index--) inputs.Add(Key(modifiers[index], true));
        try { Send(inputs); }
        finally { ReleaseModifiers(); }
        if (timing.KeyDelay > 0) await Task.Delay(timing.KeyDelay, cancellationToken);
        else await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void ReleaseModifiers()
    {
        if (_heldModifiers.Count == 0) return;
        var inputs = _heldModifiers.Select(key => Key(key, true)).ToArray();
        _heldModifiers.Clear();
        try { Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.Input>()); } catch { }
    }

    private void TypeCharacter(char character)
    {
        nint foregroundWindow = Win32.GetForegroundWindow();
        uint foregroundThread = foregroundWindow == 0 ? 0 : Win32.GetWindowThreadProcessId(foregroundWindow, out _);
        nint layout = Win32.GetKeyboardLayout(foregroundThread);
        short mapped = Win32.VkKeyScanEx(character, layout);
        if (mapped == -1)
        {
            Send([UnicodeKey(character, false), UnicodeKey(character, true)]);
            return;
        }

        ushort key = (ushort)(mapped & 0xFF);
        byte state = (byte)((mapped >> 8) & 0xFF);
        if (key is >= 'A' and <= 'Z' && (Win32.GetKeyState(0x14) & 1) != 0)
            state ^= 1;
        var modifiers = new List<ushort>(3);
        if ((state & 1) != 0) modifiers.Add(0x10);
        if ((state & 2) != 0) modifiers.Add(0x11);
        if ((state & 4) != 0) modifiers.Add(0x12);
        var inputs = new List<Win32.Input>();
        foreach (ushort modifier in modifiers) { _heldModifiers.Add(modifier); inputs.Add(Key(modifier, false)); }
        inputs.Add(Key(key, false));
        inputs.Add(Key(key, true));
        for (int index = modifiers.Count - 1; index >= 0; index--) inputs.Add(Key(modifiers[index], true));
        try { Send(inputs); }
        finally { ReleaseModifiers(); }
    }

    private static Win32.Input Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = Win32.InputKeyboard,
        Data = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { VirtualKey = virtualKey, Flags = keyUp ? Win32.KeyUp : 0 } }
    };

    private static Win32.Input UnicodeKey(char character, bool keyUp) => new()
    {
        Type = Win32.InputKeyboard,
        Data = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { ScanCode = character, Flags = Win32.Unicode | (keyUp ? Win32.KeyUp : 0) } }
    };

    private static void Send(IReadOnlyCollection<Win32.Input> inputs)
    {
        var array = inputs as Win32.Input[] ?? inputs.ToArray();
        uint sent = Win32.SendInput((uint)array.Length, array, Marshal.SizeOf<Win32.Input>());
        if (sent != (uint)array.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Windows приняла {sent} из {array.Length} событий клавиатуры (INPUT={Marshal.SizeOf<Win32.Input>()} байт).");
        }
    }
}
