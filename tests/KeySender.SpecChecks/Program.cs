using KeySender.Execution;
using KeySender.Input;
using KeySender.Models;
using KeySender.Parser;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Reflection;
using System.Windows.Threading;

if (args.Contains("--input-smoke", StringComparer.OrdinalIgnoreCase))
    return await RunInputSmokeAsync();
if (args.Contains("--hotkey-smoke", StringComparer.OrdinalIgnoreCase))
    return await RunHotkeySmokeAsync();

var parser = new ScriptParser();
var checks = new (string Name, Action Run)[]
{
    ("commands and combinations", () =>
    {
        var commands = parser.Parse("hello{ENTER}{CTRL+SHIFT+ESC}{F24}{WAIT 3600000}", false);
        Assert(commands.Count == 5, "expected five commands");
        Assert(commands[0] is TextCommand { Text: "hello" }, "text token");
        Assert(commands[1] is KeyCommand { VirtualKey: 0x0D }, "Enter key");
        Assert(commands[2] is KeyCommand { VirtualKey: 0x1B, Modifiers.Count: 2 }, "modifier combination");
        Assert(commands[3] is KeyCommand { VirtualKey: 0x87 }, "F24 key");
        Assert(commands[4] is WaitCommand { Milliseconds: 3600000 }, "maximum wait");
    }),
    ("required special keys and wait limits", () =>
    {
        string[] keys = ["ENTER", "TAB", "ESC", "BACKSPACE", "DELETE", "SPACE", "UP", "DOWN", "LEFT", "RIGHT", "HOME", "END", "PGUP", "PGDN", "INSERT", "F1", "F12"];
        foreach (string key in keys) Assert(parser.Parse($"{{{key}}}", false).Single() is KeyCommand, $"{key} should parse");
        try { parser.Parse("{WAIT 3600001}", false); throw new InvalidOperationException("WAIT over limit was accepted"); }
        catch (ScriptParseException) { }
    }),
    ("line endings only become Enter when enabled", () =>
    {
        var ignored = parser.Parse("one\r\ntwo", false);
        Assert(ignored.Count == 1 && ignored[0] is TextCommand { Text: "onetwo" }, "default newline behavior");
        var asEnter = parser.Parse("one\r\ntwo", true);
        Assert(asEnter.Count == 3 && asEnter[1] is KeyCommand { VirtualKey: 0x0D }, "newline Enter option");
    }),
    ("literal braces and TEXT blocks", () =>
    {
        var escaped = parser.Parse("{{}{}}", false);
        Assert(escaped.Count == 1 && escaped[0] is TextCommand { Text: "{}" }, "brace escaping");
        var block = parser.Parse("{TEXT}\nraw {ENTER}\n{/TEXT}{ENTER}", false);
        Assert(block.Count == 4 && block[0] is KeyCommand { VirtualKey: 0x0D } &&
               block[1] is TextCommand { Text: "raw {ENTER}" } && block[2] is KeyCommand { VirtualKey: 0x0D } &&
               block[3] is KeyCommand { VirtualKey: 0x0D }, "TEXT block preserves line breaks and literal commands");
    }),
    ("invalid syntax includes source line", () =>
    {
        try { parser.Parse("ok\n{WAIT ABC}", false); throw new InvalidOperationException("invalid WAIT was accepted"); }
        catch (ScriptParseException ex) { Assert(ex.Line == 2 && ex.Message.Contains("число", StringComparison.OrdinalIgnoreCase), "WAIT error details"); }
        try { parser.Parse("{CTRL+SUPERKEY}", false); throw new InvalidOperationException("invalid key was accepted"); }
        catch (ScriptParseException ex) { Assert(ex.Line == 1, "unknown key line"); }
    }),
    ("executor repeats commands and releases input", async () =>
    {
        var fakeKeyboard = new FakeInputProvider();
        var fakeClipboard = new FakeInputProvider();
        var executor = new ScriptExecutor(fakeKeyboard, fakeClipboard);
        var commands = parser.Parse("x{ENTER}{WAIT 0}", false);
        await executor.ExecuteAsync(commands, InputMode.Keyboard, new InputTiming(0, false, 0, 0, 0), 2, CancellationToken.None, _ => { });
        Assert(fakeKeyboard.Events.SequenceEqual(["text:x", "key:13", "text:x", "key:13"]), "repeat order");
        Assert(fakeKeyboard.Released, "keyboard modifiers released");
    }),
    ("clipboard mode pastes text and sends command keys via keyboard", async () =>
    {
        var fakeKeyboard = new FakeInputProvider();
        var fakeClipboard = new FakeInputProvider();
        var executor = new ScriptExecutor(fakeKeyboard, fakeClipboard);
        await executor.ExecuteAsync(parser.Parse("hello{ENTER}", false), InputMode.Clipboard, new InputTiming(0, false, 0, 0, 0), 1, CancellationToken.None, _ => { });
        Assert(fakeClipboard.Events.SequenceEqual(["text:hello"]), "text should use clipboard provider");
        Assert(fakeKeyboard.Events.SequenceEqual(["key:13"]), "key command should use keyboard provider");
    }),
    ("cancellation during WAIT releases held modifiers", async () =>
    {
        var fakeKeyboard = new FakeInputProvider();
        var executor = new ScriptExecutor(fakeKeyboard, new FakeInputProvider());
        using var cancellation = new CancellationTokenSource(20);
        try
        {
            await executor.ExecuteAsync(parser.Parse("{WAIT 3600000}", false), InputMode.Keyboard, new InputTiming(0, false, 0, 0, 0), 1, cancellation.Token, _ => { });
            throw new InvalidOperationException("cancelled wait completed normally");
        }
        catch (OperationCanceledException) { }
        Assert(fakeKeyboard.Released, "keyboard modifiers released after cancellation");
    }),
    ("zero-delay command streams remain cancellable", async () =>
    {
        var fakeKeyboard = new FakeInputProvider();
        var executor = new ScriptExecutor(fakeKeyboard, new FakeInputProvider());
        using var cancellation = new CancellationTokenSource();
        var commands = Enumerable.Repeat<ScriptCommand>(new WaitCommand(0, 1), 1_000_000).ToArray();
        Task cancel = Task.Run(async () =>
        {
            await Task.Delay(10);
            cancellation.Cancel();
        });
        try
        {
            await executor.ExecuteAsync(commands, InputMode.Keyboard, new InputTiming(0, false, 0, 0, 0), 1, cancellation.Token, _ => { });
            throw new InvalidOperationException("zero-delay execution ignored cancellation");
        }
        catch (OperationCanceledException) { }
        await cancel;
        Assert(fakeKeyboard.Released, "modifiers should be released after zero-delay cancellation");
    })
};

int failed = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{checks.Length - failed}/{checks.Length} checks passed");
return failed == 0 ? 0 : 1;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static Task<int> RunInputSmokeAsync()
{
    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var uiThread = new Thread(() =>
    {
        int result = 0;
        try
        {
            var app = new Application();
            var textBox = new TextBox { FontSize = 18, Margin = new Thickness(16), AcceptsReturn = true, Text = "Щёлкните сюда для проверки клавиатурного ввода" };
            var window = new Window
            {
                Title = "KeySender SendInput check",
                Width = 560,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Щёлкните поле ниже в течение 15 секунд. Тест проверит английские и русские символы, пунктуацию и Ctrl+A.", Margin = new Thickness(16, 14, 16, 0), TextWrapping = TextWrapping.Wrap },
                        textBox
                    }
                }
            };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    window.Activate(); textBox.Focus();
                    nint windowHandle = new WindowInteropHelper(window).Handle;
                    bool isForeground = false;
                    for (int attempt = 0; attempt < 60; attempt++)
                    {
                        if (User32.GetForegroundWindow() == windowHandle) { isForeground = true; break; }
                        await Task.Delay(250);
                    }
                    Assert(isForeground, "the input test window did not receive foreground focus; click its text field and try again");

                    textBox.Clear(); textBox.Focus();
                    var keyboard = new KeyboardInputProvider();
                    var timing = new InputTiming(2, false, 0, 0, 0);
                    const string expected = "Hello World! abc XYZ 0123 /\\-_. ,:;@#$%^&*()[]{}=+'\" Привет";
                    await keyboard.TypeTextAsync(expected, timing, CancellationToken.None);
                    await Task.Delay(100);
                    Assert(textBox.Text == expected, $"Keyboard text mismatch. Expected [{expected}], received [{textBox.Text}]");

                    textBox.Clear(); textBox.Focus();
                    await keyboard.TypeTextAsync("replace me", timing, CancellationToken.None);
                    await keyboard.PressKeyAsync(0x41, [0x11], timing, CancellationToken.None);
                    await keyboard.TypeTextAsync("selected", timing, CancellationToken.None);
                    await Task.Delay(100);
                    Assert(textBox.Text == "selected", "Ctrl+A should select all text before replacement");
                    Console.WriteLine("PASS SendInput text, shifted punctuation, Unicode fallback, and Ctrl+A in focused WPF TextBox");
                }
                catch (Exception ex)
                {
                    result = 1; Console.Error.WriteLine($"FAIL SendInput smoke: {ex.Message} Win32={((ex as Win32Exception)?.NativeErrorCode.ToString() ?? "n/a")}");
                }
                finally { window.Close(); }
            };
            app.Run(window);
        }
        catch (Exception ex)
        {
            result = 1; Console.Error.WriteLine($"FAIL SendInput smoke startup: {ex.Message}");
        }
        completion.TrySetResult(result);
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    return completion.Task;
}

static Task<int> RunHotkeySmokeAsync()
{
    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var uiThread = new Thread(() =>
    {
        int result = 0;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            string settingsPath = Path.Combine(Environment.CurrentDirectory, ".dotnet-cli-home", "hotkey-smoke", "settings.json");
            var window = new KeySender.MainWindow(new KeySender.Services.SettingsService(settingsPath));
            window.Loaded += (_, _) =>
            {
                try
                {
                    TextBox scriptBox = GetWindowField<TextBox>(window, "ScriptBox");
                    TextBox startDelayBox = GetWindowField<TextBox>(window, "StartDelayBox");
                    TextBox keyDelayBox = GetWindowField<TextBox>(window, "KeyDelayBox");
                    TextBox enterDelayBox = GetWindowField<TextBox>(window, "EnterDelayBox");
                    CheckBox minimizeCheck = GetWindowField<CheckBox>(window, "MinimizeCheck");
                    TextBlock statusText = GetWindowField<TextBlock>(window, "StatusText");

                    if (statusText.Text.StartsWith("Не удалось зарегистрировать", StringComparison.Ordinal))
                        throw new InvalidOperationException(statusText.Text);

                    scriptBox.Text = "{WAIT 3600000}";
                    startDelayBox.Text = "0";
                    keyDelayBox.Text = "0";
                    enterDelayBox.Text = "0";
                    minimizeCheck.IsChecked = false;
                    typeof(KeySender.MainWindow).GetMethod("Play_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [window, new RoutedEventArgs()]);

                    var sendHotkey = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    sendHotkey.Tick += (_, _) =>
                    {
                        sendHotkey.Stop();
                        try
                        {
                            nint handle = new WindowInteropHelper(window).Handle;
                            if (!User32.PostMessage(handle, 0x0312, (nint)0x4B53, (nint)(0x77u << 16)))
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not post WM_HOTKEY.");
                        }
                        catch (Exception ex) { result = 1; Console.Error.WriteLine($"FAIL hotkey message dispatch: {ex.Message}"); window.Close(); return; }

                        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                        var observe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                        observe.Tick += (_, _) =>
                        {
                            if (statusText.Text == "Остановлено пользователем")
                            {
                                observe.Stop();
                                Console.WriteLine("PASS F8 registration and WM_HOTKEY cancellation path");
                                window.Close();
                            }
                            else if (DateTime.UtcNow >= deadline)
                            {
                                observe.Stop(); result = 1;
                                Console.Error.WriteLine($"FAIL global F8 did not stop the script; status=[{statusText.Text}]");
                                window.Close();
                            }
                        };
                        observe.Start();
                    };
                    sendHotkey.Start();
                }
                catch (Exception ex)
                {
                    result = 1;
                    Console.Error.WriteLine($"FAIL global F8 smoke: {ex.Message}");
                    window.Close();
                }
            };
            app.Run(window);
        }
        catch (Exception ex)
        {
            result = 1;
            Console.Error.WriteLine($"FAIL global F8 smoke startup: {ex.Message}");
        }
        completion.TrySetResult(result);
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    return completion.Task;
}

static T GetWindowField<T>(KeySender.MainWindow window, string name) where T : class =>
    typeof(KeySender.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as T
    ?? throw new InvalidOperationException($"Control {name} was not found.");

sealed class FakeInputProvider : IInputProvider
{
    public List<string> Events { get; } = [];
    public bool Released { get; private set; }

    public Task TypeTextAsync(string text, InputTiming timing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Events.Add($"text:{text}"); return Task.CompletedTask;
    }

    public Task PressKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifiers, InputTiming timing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Events.Add($"key:{virtualKey}"); return Task.CompletedTask;
    }

    public void ReleaseModifiers() => Released = true;
}

static class User32
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
