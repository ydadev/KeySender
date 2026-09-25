namespace KeySender.Models;

public sealed class AppSettings
{
    public int StartDelay { get; set; } = 5;
    public int KeyDelay { get; set; } = 50;
    public int EnterDelay { get; set; } = 100;
    public bool RandomDelayEnabled { get; set; }
    public int RandomDelayMin { get; set; } = 40;
    public int RandomDelayMax { get; set; } = 70;
    public int RepeatCount { get; set; } = 1;
    public InputMode InputMode { get; set; } = InputMode.Keyboard;
    public bool MinimizeOnPlay { get; set; } = true;
    public string EmergencyHotkey { get; set; } = "F8";
    public bool NewLineIsEnter { get; set; }
    public int WindowWidth { get; set; } = 920;
    public int WindowHeight { get; set; } = 680;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public List<string> RecentFiles { get; set; } = [];
}
