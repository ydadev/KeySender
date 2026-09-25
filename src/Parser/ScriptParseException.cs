namespace KeySender.Parser;

public sealed class ScriptParseException(int line, string message) : Exception(message)
{
    public int Line { get; } = line;
}
