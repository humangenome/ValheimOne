using HarmonyLib;

namespace ValheimOne.Networking;

public interface IVersionHandshake
{
    bool IsAvailable { get; }

    void Initialize(Harmony harmony);

    void Shutdown();
}
