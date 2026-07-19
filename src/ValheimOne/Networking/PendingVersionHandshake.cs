namespace ValheimOne.Networking;

public sealed class PendingVersionHandshake : IVersionHandshake
{
    public bool IsAvailable => false;

    public void Initialize()
    {
        // TODO: Register a namespaced ZRoutedRpc request/response after the game's routed RPC is ready.
        // TODO: Exchange plugin version, supported game version, and NetworkConfigSchema before syncing rules.
        // TODO: Let the server reject incompatible required clients with a clear, actionable message.
    }

    public void Shutdown()
    {
        // TODO: Unregister handlers and discard peer compatibility state when the game exposes a safe lifecycle hook.
    }
}
