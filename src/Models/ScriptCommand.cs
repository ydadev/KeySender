namespace KeySender.Models;

public abstract record ScriptCommand(int Line);
public sealed record TextCommand(string Text, int SourceLine) : ScriptCommand(SourceLine);
public sealed record KeyCommand(ushort VirtualKey, IReadOnlyList<ushort> Modifiers, string Name, int SourceLine) : ScriptCommand(SourceLine);
public sealed record WaitCommand(int Milliseconds, int SourceLine) : ScriptCommand(SourceLine);

public enum InputMode
{
    Keyboard,
    Clipboard
}
