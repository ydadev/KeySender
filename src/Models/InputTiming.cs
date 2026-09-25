namespace KeySender.Models;

public sealed record InputTiming(int KeyDelay, bool RandomDelayEnabled, int RandomDelayMin, int RandomDelayMax, int EnterDelay)
{
    public int NextCharacterDelay() => RandomDelayEnabled
        ? Random.Shared.Next(RandomDelayMin, RandomDelayMax + 1)
        : KeyDelay;
}
