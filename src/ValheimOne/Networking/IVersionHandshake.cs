namespace ValheimOne.Networking;

public interface IVersionHandshake
{
    bool IsAvailable { get; }

    void Initialize();

    void Shutdown();
}
