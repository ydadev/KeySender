using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KeySender.Execution;
using KeySender.Input;
using KeySender.Models;
using KeySender.Native;
using KeySender.Parser;
using KeySender.Services;
using Microsoft.Win32;

namespace KeySender;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ScriptParser _parser = new();
    private readonly KeyboardInputProvider _keyboard = new();
    private readonly ClipboardInputProvider _clipboard;
    private readonly ScriptExecutor _executor;
    private AppSettings _settings;
    private CancellationTokenSource? _executionCancellation;
    private HwndSource? _source;
    private string? _currentFile;
    private bool _isExecuting;
    private bool _closeAfterStop;

    public MainWindow() : this(new SettingsService())
    {
    }

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        _clipboard = new ClipboardInputProvider(_keyboard);
        _executor = new ScriptExecutor(_keyboard, _clipboard);
        _settings = _settingsService.Load();
        _settings.RecentFiles ??= [];
        _settings.EmergencyHotkey ??= "F8";
        ApplySettings();
        AppLogger.Write("Application started");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WindowProc);
        ushort hotkey = ResolveEmergencyHotkey(_settings.EmergencyHotkey);
        if (hotkey == 0x77 && !string.Equals(_settings.EmergencyHotkey, "F8", StringComparison.OrdinalIgnoreCase))
            _settings.EmergencyHotkey = "F8";
        EmergencyHotkeyText.Text = $"{_settings.EmergencyHotkey} — аварийная остановка";
        if (!Win32.RegisterHotKey(handle, Win32.HotkeyId, Win32.ModNoRepeat, hotkey))
            SetStatus($"Не удалось зарегистрировать {_settings.EmergencyHotkey} — возможно, клавиша уже занята.");
        RebuildRecentMenu();
    }

    private static ushort ResolveEmergencyHotkey(string? value)
    {
        if (value is { Length: >= 2 and <= 3 } && value[0] is 'F' or 'f' &&
            int.TryParse(value[1..], out int functionNumber) && functionNumber is >= 1 and <= 24)
            return (ushort)(0x70 + functionNumber - 1);
        return 0x77;
    }

    private void ApplySettings()
    {
        Width = Math.Clamp(_settings.WindowWidth, 700, 1600);
        Height = Math.Clamp(_settings.WindowHeight, 520, 1200);
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top;
        }
        StartDelayBox.Text = Math.Clamp(_settings.StartDelay, 0, 60).ToString();
        KeyDelayBox.Text = Math.Clamp(_settings.KeyDelay, 0, 1000).ToString();
        EnterDelayBox.Text = Math.Clamp(_settings.EnterDelay, 0, 60000).ToString();
        RandomDelayCheck.IsChecked = _settings.RandomDelayEnabled;
        RandomMinBox.Text = Math.Clamp(_settings.RandomDelayMin, 0, 1000).ToString();
        RandomMaxBox.Text = Math.Clamp(_settings.RandomDelayMax, 0, 1000).ToString();
        RepeatCountBox.Text = Math.Clamp(_settings.RepeatCount, 1, 1000).ToString();
        InputModeBox.SelectedIndex = _settings.InputMode == InputMode.Clipboard ? 1 : 0;
        MinimizeCheck.IsChecked = _settings.MinimizeOnPlay;
        NewLineCheck.IsChecked = _settings.NewLineIsEnter;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting) return;
        if (!int.TryParse(StartDelayBox.Text, out int startDelay) || startDelay is < 0 or > 60)
        { SetStatus("Ошибка: задержка старта должна быть от 0 до 60 секунд."); return; }
        if (!int.TryParse(KeyDelayBox.Text, out int keyDelay) || keyDelay is < 0 or > 1000)
        { SetStatus("Ошибка: интервал клавиш должен быть от 0 до 1000 мс."); return; }
        if (!int.TryParse(EnterDelayBox.Text, out int enterDelay) || enterDelay is < 0 or > 60000)
        { SetStatus("Ошибка: задержка после Enter должна быть от 0 до 60000 мс."); return; }
        int randomMin = _settings.RandomDelayMin;
        int randomMax = _settings.RandomDelayMax;
        bool randomDelayEnabled = RandomDelayCheck.IsChecked == true;
        int parsedRandomMin = 0, parsedRandomMax = 0;
        bool randomRangeIsValid = int.TryParse(RandomMinBox.Text, out parsedRandomMin) && parsedRandomMin is >= 0 and <= 1000 &&
                                  int.TryParse(RandomMaxBox.Text, out parsedRandomMax) && parsedRandomMax is >= 0 and <= 1000 &&
                                  parsedRandomMin <= parsedRandomMax;
        if (randomDelayEnabled && !randomRangeIsValid)
        { SetStatus("Ошибка: диапазон случайной задержки должен быть от 0 до 1000 мс."); return; }
        if (randomRangeIsValid) { randomMin = parsedRandomMin; randomMax = parsedRandomMax; }
        if (!int.TryParse(RepeatCountBox.Text, out int repeatCount) || repeatCount is < 1 or > 1000)
        { SetStatus("Ошибка: число повторов должно быть от 1 до 1000."); return; }

        IReadOnlyList<ScriptCommand> commands;
        try { commands = _parser.Parse(ScriptBox.Text, NewLineCheck.IsChecked == true); }
        catch (ScriptParseException ex) { AppLogger.Write("Parser error"); SetStatus($"Ошибка в строке {ex.Line}: {ex.Message}"); return; }
        if (commands.Count == 0) { SetStatus("Сценарий пуст."); return; }

        _settings.StartDelay = startDelay; _settings.KeyDelay = keyDelay; _settings.EnterDelay = enterDelay;
        _settings.RandomDelayEnabled = randomDelayEnabled;
        _settings.RandomDelayMin = randomMin; _settings.RandomDelayMax = randomMax; _settings.RepeatCount = repeatCount;
        _settings.InputMode = InputModeBox.SelectedIndex == 1 ? InputMode.Clipboard : InputMode.Keyboard;
        _settings.MinimizeOnPlay = MinimizeCheck.IsChecked == true;
        _settings.NewLineIsEnter = NewLineCheck.IsChecked == true;
        SaveSettings();
        _executionCancellation = new CancellationTokenSource();
        AppLogger.Write("Script started");
        CancellationToken token = _executionCancellation.Token;
        _isExecuting = true;
        PlayButton.IsEnabled = false; StopButton.IsEnabled = true;
        if (_settings.MinimizeOnPlay) WindowState = WindowState.Minimized;

        try
        {
            for (int seconds = startDelay; seconds > 0; seconds--)
            {
                SetStatus($"Старт через {seconds} секунд");
                await Task.Delay(1000, token);
            }
            SetStatus("Выполняется");
            var timing = new InputTiming(keyDelay, randomDelayEnabled, randomMin, randomMax, enterDelay);
            await _executor.ExecuteAsync(commands, _settings.InputMode, timing, repeatCount, token, SetStatus);
            AppLogger.Write("Script completed");
            SetStatus("Выполнено");
        }
        catch (OperationCanceledException) { AppLogger.Write("Script cancelled"); SetStatus("Остановлено пользователем"); }
        catch (Exception ex)
        {
            AppLogger.Write($"Unhandled exception: {ex.GetType().Name}");
            SetStatus("Ошибка выполнения сценария");
            MessageBox.Show(this, ex.Message, "Ошибка KeySender", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _keyboard.ReleaseModifiers();
            _executionCancellation?.Dispose(); _executionCancellation = null;
            _isExecuting = false; PlayButton.IsEnabled = true; StopButton.IsEnabled = false;
            if (_closeAfterStop)
            {
                _closeAfterStop = false;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopExecution();
    private void StopExecution()
    {
        _executionCancellation?.Cancel();
        _keyboard.ReleaseModifiers();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Win32.WmHotkey && wParam.ToInt32() == Win32.HotkeyId)
        {
            StopExecution();
            handled = true;
        }
        return 0;
    }

    private void InsertCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command }) return;
        int start = ScriptBox.SelectionStart;
        ScriptBox.SelectedText = command;
        ScriptBox.SelectionStart = start + command.Length;
        ScriptBox.Focus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Сценарии KeySender (*.kscript)|*.kscript|Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*", DefaultExt = ".kscript" };
        if (dialog.ShowDialog(this) != true) return;
        try { ScriptBox.Text = File.ReadAllText(dialog.FileName); _currentFile = dialog.FileName; AddRecent(dialog.FileName); SetStatus($"Открыт: {Path.GetFileName(dialog.FileName)}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null) { SaveAs_Click(sender, e); return; }
        try
        {
            File.WriteAllText(_currentFile, ScriptBox.Text);
            AddRecent(_currentFile);
            SetStatus($"Сохранено: {Path.GetFileName(_currentFile)}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Сценарии KeySender (*.kscript)|*.kscript|Текстовые файлы (*.txt)|*.txt", DefaultExt = ".kscript", AddExtension = true, FileName = _currentFile is null ? "scenario.kscript" : Path.GetFileName(_currentFile) };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, ScriptBox.Text); _currentFile = dialog.FileName; AddRecent(dialog.FileName); SetStatus($"Сохранено: {Path.GetFileName(dialog.FileName)}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AddRecent(string path)
    {
        _settings.RecentFiles.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, path);
        if (_settings.RecentFiles.Count > 10) _settings.RecentFiles.RemoveRange(10, _settings.RecentFiles.Count - 10);
        SaveSettings(); RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var existing = _settings.RecentFiles.Where(File.Exists).ToList();
        if (existing.Count == 0) { RecentMenu.Items.Add(new MenuItem { Header = "Нет недавних файлов", IsEnabled = false }); return; }
        foreach (string path in existing)
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, Tag = path };
            item.Click += RecentFile_Click; RecentMenu.Items.Add(item);
        }
    }

    private void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path }) return;
        try { ScriptBox.Text = File.ReadAllText(path); _currentFile = path; SetStatus($"Открыт: {Path.GetFileName(path)}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, $"Обычный текст вводится посимвольно. Enter выполняется только командой {{ENTER}} (если не включён перенос строк = Enter).\n\nКоманды: {{ENTER}}, {{TAB}}, {{ESC}}, {{WAIT 1000}}, {{CTRL+C}}, {{ALT+F4}}, {{WIN+R}}, {{F1}}…{{F24}}.\n\nДля буквальных фигурных скобок: {{{{}} для {{ и {{}}}} для }}. Внутри {{TEXT}}…{{/TEXT}} команды считаются обычным текстом.\n\n{_settings.EmergencyHotkey} останавливает ввод глобально.", "Справка KeySender", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && !_isExecuting)
        {
            e.Handled = true;
            Play_Click(this, new RoutedEventArgs());
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExecuting)
        {
            e.Cancel = true;
            _closeAfterStop = true;
            StopExecution();
            return;
        }
        StopExecution();
        if (_source is not null) _source.RemoveHook(WindowProc);
        Win32.UnregisterHotKey(new WindowInteropHelper(this).Handle, Win32.HotkeyId);
        CaptureSettingsFromControls();
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowWidth = (int)bounds.Width; _settings.WindowHeight = (int)bounds.Height;
        _settings.WindowLeft = bounds.Left; _settings.WindowTop = bounds.Top;
        SaveSettings();
    }

    private void CaptureSettingsFromControls()
    {
        if (int.TryParse(StartDelayBox.Text, out int startDelay) && startDelay is >= 0 and <= 60)
            _settings.StartDelay = startDelay;
        if (int.TryParse(KeyDelayBox.Text, out int keyDelay) && keyDelay is >= 0 and <= 1000)
            _settings.KeyDelay = keyDelay;
        if (int.TryParse(EnterDelayBox.Text, out int enterDelay) && enterDelay is >= 0 and <= 60000)
            _settings.EnterDelay = enterDelay;
        if (int.TryParse(RandomMinBox.Text, out int randomMin) && randomMin is >= 0 and <= 1000 &&
            int.TryParse(RandomMaxBox.Text, out int randomMax) && randomMax is >= 0 and <= 1000 && randomMin <= randomMax)
        {
            _settings.RandomDelayMin = randomMin;
            _settings.RandomDelayMax = randomMax;
        }
        if (int.TryParse(RepeatCountBox.Text, out int repeatCount) && repeatCount is >= 1 and <= 1000)
            _settings.RepeatCount = repeatCount;
        _settings.RandomDelayEnabled = RandomDelayCheck.IsChecked == true;
        _settings.InputMode = InputModeBox.SelectedIndex == 1 ? InputMode.Clipboard : InputMode.Keyboard;
        _settings.MinimizeOnPlay = MinimizeCheck.IsChecked == true;
        _settings.NewLineIsEnter = NewLineCheck.IsChecked == true;
    }

    private void SaveSettings()
    {
        try { _settingsService.Save(_settings); } catch { }
    }

    private void SetStatus(string status) => StatusText.Text = status;
}
