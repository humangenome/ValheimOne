using BepInEx.Logging;

namespace ValheimOne.Infrastructure;

public sealed class ModLogger
{
    private readonly ManualLogSource _source;

    public ModLogger(ManualLogSource source)
    {
        _source = source;
    }

    public void Debug(string message) => _source.LogDebug(message);

    public void Info(string message) => _source.LogInfo(message);

    public void Warning(string message) => _source.LogWarning(message);

    public void Error(string message) => _source.LogError(message);
}
