using System.Globalization;
using KeySender.Models;

namespace KeySender.Parser;

public sealed class ScriptParser
{
    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = 0x0D, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B,
        ["BACKSPACE"] = 0x08, ["DELETE"] = 0x2E, ["SPACE"] = 0x20,
        ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
        ["HOME"] = 0x24, ["END"] = 0x23, ["PGUP"] = 0x21, ["PGDN"] = 0x22,
        ["INSERT"] = 0x2D
    };

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12, ["WIN"] = 0x5B
    };

    public IReadOnlyList<ScriptCommand> Parse(string script, bool newLineIsEnter)
    {
        var commands = new List<ScriptCommand>();
        var text = new System.Text.StringBuilder();
        int textLine = 1;
        int line = 1;
        bool inTextBlock = false;

        void FlushText()
        {
            if (text.Length > 0)
            {
                commands.Add(new TextCommand(text.ToString(), textLine));
                text.Clear();
            }
        }

        void AddText(string value, int sourceLine)
        {
            if (text.Length == 0) textLine = sourceLine;
            text.Append(value);
        }

        for (int i = 0; i < script.Length;)
        {
            char current = script[i];
            if (current == '\r') { i++; continue; }
            if (current == '\n')
            {
                if (inTextBlock || newLineIsEnter)
                {
                    FlushText();
                    commands.Add(new KeyCommand(0x0D, [], "ENTER", line));
                }
                line++;
                i++;
                continue;
            }

            if (!inTextBlock && current == '{' && i + 2 < script.Length && script.AsSpan(i).StartsWith("{{}"))
            {
                AddText("{", line); i += 3; continue;
            }
            if (!inTextBlock && current == '{' && i + 2 < script.Length && script.AsSpan(i).StartsWith("{}}"))
            {
                AddText("}", line); i += 3; continue;
            }

            if (current != '{') { AddText(current.ToString(), line); i++; continue; }
            int close = script.IndexOf('}', i + 1);
            if (close < 0) throw new ScriptParseException(line, "Не найдена закрывающая скобка '}'.");
            string token = script[(i + 1)..close].Trim();
            if (inTextBlock)
            {
                if (token.Equals("/TEXT", StringComparison.OrdinalIgnoreCase)) inTextBlock = false;
                else AddText(script[i..(close + 1)], line);
                i = close + 1;
                continue;
            }

            if (token.Equals("TEXT", StringComparison.OrdinalIgnoreCase))
            {
                FlushText(); inTextBlock = true; i = close + 1; continue;
            }
            FlushText();
            if (token.StartsWith("WAIT", StringComparison.OrdinalIgnoreCase))
            {
                string value = token.Length > 4 ? token[4..].Trim() : string.Empty;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds))
                    throw new ScriptParseException(line, "WAIT ожидает число миллисекунд.");
                if (milliseconds < 0 || milliseconds > 3_600_000)
                    throw new ScriptParseException(line, "WAIT должен быть в диапазоне 0–3600000 мс.");
                commands.Add(new WaitCommand(milliseconds, line));
            }
            else commands.Add(ParseKey(token, line));
            i = close + 1;
        }

        if (inTextBlock) throw new ScriptParseException(line, "Не найден закрывающий блок {/TEXT}.");
        FlushText();
        return commands;
    }

    private static KeyCommand ParseKey(string token, int line)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ScriptParseException(line, "Пустая команда в фигурных скобках.");
        string[] parts = token.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        for (int index = 0; index < parts.Length - 1; index++)
        {
            if (!Modifiers.TryGetValue(parts[index], out ushort modifier))
                throw new ScriptParseException(line, $"Неизвестный модификатор {parts[index]}.");
            if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
        }

        string keyName = parts[^1];
        if (Keys.TryGetValue(keyName, out ushort key)) return new KeyCommand(key, modifiers, keyName.ToUpperInvariant(), line);
        if (keyName.Length is >= 2 and <= 3 && keyName[0] is 'F' or 'f' && int.TryParse(keyName[1..], out int functionNumber) && functionNumber is >= 1 and <= 24)
            return new KeyCommand((ushort)(0x70 + functionNumber - 1), modifiers, keyName.ToUpperInvariant(), line);
        if (keyName.Length == 1)
        {
            char ch = char.ToUpperInvariant(keyName[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return new KeyCommand(ch, modifiers, keyName.ToUpperInvariant(), line);
        }
        throw new ScriptParseException(line, $"Неизвестная клавиша {keyName}.");
    }
}
