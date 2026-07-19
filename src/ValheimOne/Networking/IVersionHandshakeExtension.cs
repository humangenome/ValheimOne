using System.Collections.Generic;

namespace ValheimOne.Networking;

public interface IVersionHandshakeExtension
{
    void RegisterRpcHandlers(ZRoutedRpc routedRpc);

    void OnPeerCompatible(long peerId);

    void Pump(ZNet net, ZRoutedRpc routedRpc, IReadOnlyCollection<long> compatiblePeerIds);

    bool TryHandleAcknowledgement(long sender, string channel, int index);

    void ResetNetworkState();

    void Shutdown();
}
