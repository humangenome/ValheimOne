using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Networking;

public sealed class VersionHandshake : IVersionHandshake
{
    private const string HelloRpc = "VO_Hello";
    private const string ConfigRpc = "VO_Config";
    private const string AckRpc = "VO_Ack";
    private const int MaximumConfigChunks = 1024;

    private static VersionHandshake? _active;

    private readonly ValheimOneConfig _settings;
    private readonly ServerConfig _serverConfig;
    private readonly ModLogger _log;
    private readonly Dictionary<long, PeerHandshakeState> _peerStates =
        new Dictionary<long, PeerHandshakeState>();
    private readonly List<ZRoutedRpc> _registeredRpcInstances = new List<ZRoutedRpc>();
    private readonly List<ZRoutedRpc> _failedRpcInstances = new List<ZRoutedRpc>();

    private ClientConfigBuffer? _clientConfigBuffer;
    private bool _clientHelloFailureLogged;
    private bool _clientHelloSent;
    private bool _initialized;

    public VersionHandshake(
        ValheimOneConfig settings,
        ServerConfig serverConfig,
        ModLogger log)
    {
        _settings = settings;
        _serverConfig = serverConfig;
        _log = log;
    }

    public bool IsAvailable =>
        ReferenceEquals(_active, this) && _registeredRpcInstances.Count != 0;

    public void Initialize(Harmony harmony)
    {
        if (_initialized)
        {
            return;
        }

        _active = this;
        PatchPostfix(harmony, typeof(Game), nameof(Game.Start), Type.EmptyTypes, nameof(GameStartPostfix));
        PatchPostfix(
            harmony,
            typeof(ZNet),
            nameof(ZNet.RPC_PeerInfo),
            new[] { typeof(ZRpc), typeof(ZPackage) },
            nameof(PeerInfoPostfix));
        PatchPostfix(
            harmony,
            typeof(ZNet),
            nameof(ZNet.SendPeriodicData),
            new[] { typeof(float) },
            nameof(SendPeriodicDataPostfix));
        PatchPostfix(
            harmony,
            typeof(ZNet),
            nameof(ZNet.OnDestroy),
            Type.EmptyTypes,
            nameof(ZNetOnDestroyPostfix));

        _initialized = true;
        _log.Info("Server enforcement chassis ready (VO_Hello, VO_Config, VO_Ack).");
    }

    public void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        _settings.ClearOverlay();
        _peerStates.Clear();
        _clientConfigBuffer = null;
        _clientHelloFailureLogged = false;
        _clientHelloSent = false;
        _registeredRpcInstances.Clear();
        _failedRpcInstances.Clear();
        _initialized = false;
    }

    private static void PatchPostfix(
        Harmony harmony,
        Type declaringType,
        string methodName,
        Type[] parameterTypes,
        string postfixName)
    {
        var original = AccessTools.Method(declaringType, methodName, parameterTypes)
            ?? throw new MissingMethodException(declaringType.FullName, methodName);
        var postfix = new HarmonyMethod(typeof(VersionHandshake), postfixName);
        harmony.Patch(original, postfix: postfix);
    }

    private static void GameStartPostfix()
    {
        VersionHandshake? active = _active;
        if (active == null)
        {
            return;
        }

        active.RegisterRpcHandlers();
        ZNet? net = ZNet.instance;
        if (net != null && !net.IsServer())
        {
            active.TrySendClientHello(net);
        }
    }

    private static void PeerInfoPostfix(ZNet __instance)
    {
        if (!__instance.IsServer())
        {
            _active?.TrySendClientHello(__instance);
        }
    }

    private static void SendPeriodicDataPostfix(ZNet __instance)
    {
        VersionHandshake? active = _active;
        if (active == null)
        {
            return;
        }

        active.RegisterRpcHandlers();
        if (__instance.IsServer())
        {
            active.PumpServer(__instance);
        }
        else if (!active._clientHelloSent)
        {
            active.TrySendClientHello(__instance);
        }
    }

    private static void ZNetOnDestroyPostfix()
    {
        _active?.ResetNetworkState();
    }

    private void RegisterRpcHandlers()
    {
        if (!ReferenceEquals(_active, this))
        {
            return;
        }

        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || IsRegistered(routedRpc) || IsRegistrationFailed(routedRpc))
        {
            return;
        }

        try
        {
            routedRpc.Register<ZPackage>(HelloRpc, HandleHello);
            routedRpc.Register<ZPackage>(ConfigRpc, HandleConfig);
            routedRpc.Register<ZPackage>(AckRpc, HandleAck);
            _registeredRpcInstances.Add(routedRpc);
            _log.Debug("Registered ValheimOne routed RPC handlers.");
        }
        catch (Exception exception)
        {
            _failedRpcInstances.Add(routedRpc);
            _log.Error($"Unable to register ValheimOne routed RPCs: {exception.Message}");
        }
    }

    private bool IsRegistered(ZRoutedRpc routedRpc)
    {
        foreach (ZRoutedRpc registered in _registeredRpcInstances)
        {
            if (ReferenceEquals(registered, routedRpc))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegistrationFailed(ZRoutedRpc routedRpc)
    {
        foreach (ZRoutedRpc failed in _failedRpcInstances)
        {
            if (ReferenceEquals(failed, routedRpc))
            {
                return true;
            }
        }

        return false;
    }

    private void TrySendClientHello(ZNet net)
    {
        if (_clientHelloSent || net.IsServer())
        {
            return;
        }

        RegisterRpcHandlers();
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        ZNetPeer? serverPeer = net.GetServerPeer();
        if (routedRpc == null || !IsRegistered(routedRpc) || serverPeer == null || !serverPeer.IsReady())
        {
            return;
        }

        long serverPeerId = serverPeer.m_uid;
        if (serverPeerId == 0L)
        {
            return;
        }

        try
        {
            string effectiveConfig = ConfigSyncSerializer.Serialize(_settings.Features);
            var package = new ZPackage();
            package.Write(VersionInfo.PluginVersion);
            package.Write(VersionInfo.NetworkConfigSchema);
            package.Write(ConfigSyncSerializer.ComputeHash(effectiveConfig));
            routedRpc.InvokeRoutedRPC(serverPeerId, HelloRpc, package);
            _clientHelloSent = true;
            _log.Info(
                $"Sent {HelloRpc} to server (ValheimOne v{VersionInfo.PluginVersion}, " +
                $"schema {VersionInfo.NetworkConfigSchema}).");
        }
        catch (Exception exception)
        {
            if (!_clientHelloFailureLogged)
            {
                _clientHelloFailureLogged = true;
                _log.Warning($"Unable to send {HelloRpc} to the server: {exception.Message}");
            }
        }
    }

    private void HandleHello(long sender, ZPackage package)
    {
        ZNet? net = ZNet.instance;
        if (!ReferenceEquals(_active, this) || net == null || !net.IsServer())
        {
            return;
        }

        ZNetPeer? peer = net.GetPeer(sender);
        if (peer == null || peer.m_server)
        {
            _log.Warning($"Ignored {HelloRpc} from unknown peer uid {sender}.");
            return;
        }

        string remoteVersion;
        int remoteSchema;
        string remoteConfigHash;
        try
        {
            remoteVersion = package.ReadString();
            remoteSchema = package.ReadInt();
            remoteConfigHash = package.ReadString();
        }
        catch (Exception exception)
        {
            HandleMalformedHello(net, peer, exception.Message);
            return;
        }

        if (remoteVersion.Length > 64 || remoteConfigHash.Length > 128)
        {
            HandleMalformedHello(net, peer, "version or config hash exceeds the protocol limit");
            return;
        }

        PeerHandshakeState state = GetOrCreateState(peer);
        if (state.HelloReceived)
        {
            return;
        }

        state.HelloReceived = true;
        state.RemoteVersion = remoteVersion;
        state.RemoteSchema = remoteSchema;
        state.RemoteConfigHash = remoteConfigHash;
        state.Compatible = VersionInfo.IsCompatible(remoteVersion, remoteSchema);

        if (!state.Compatible)
        {
            string reason =
                $"incompatible ValheimOne handshake (client v{remoteVersion}, schema {remoteSchema}; " +
                $"server v{VersionInfo.PluginVersion}, schema {VersionInfo.NetworkConfigSchema})";
            if (IsEnforcementEnabled)
            {
                KickPeer(net, state, reason);
            }
            else
            {
                _log.Info(
                    $"Peer {PeerLabel(peer)} is modded but {reason}; allowed because " +
                    "Server.EnforceMod=false.");
            }

            return;
        }

        string serverConfig;
        string serverHash;
        try
        {
            serverConfig = ConfigSyncSerializer.Serialize(_settings.Features);
            serverHash = ConfigSyncSerializer.ComputeHash(serverConfig);
        }
        catch (Exception exception)
        {
            _log.Error(
                $"Handshake with peer {PeerLabel(peer)} succeeded, but server config " +
                $"serialization failed: {exception.Message}");
            return;
        }

        string hashStatus = string.Equals(
            serverHash,
            remoteConfigHash,
            StringComparison.OrdinalIgnoreCase)
            ? "config already matches"
            : "server config will win";
        _log.Info(
            $"Peer {PeerLabel(peer)} is modded: handshake ok " +
            $"(ValheimOne v{remoteVersion}, schema {remoteSchema}; {hashStatus}).");

        if (_serverConfig.Enabled && _serverConfig.SyncConfig.Value)
        {
            QueueConfigPush(state, serverConfig);
        }
    }

    private void HandleMalformedHello(ZNet net, ZNetPeer peer, string detail)
    {
        PeerHandshakeState state = GetOrCreateState(peer);
        state.HelloReceived = true;
        state.Compatible = false;
        string reason = $"malformed ValheimOne handshake ({detail})";
        if (IsEnforcementEnabled)
        {
            KickPeer(net, state, reason);
        }
        else
        {
            _log.Info(
                $"Peer {PeerLabel(peer)} sent a {reason}; allowed because Server.EnforceMod=false.");
        }
    }

    private void QueueConfigPush(PeerHandshakeState state, string serializedConfig)
    {
        IReadOnlyList<string> chunks;
        try
        {
            chunks = ConfigSyncSerializer.CreateChunks(serializedConfig);
        }
        catch (Exception exception)
        {
            _log.Error($"Unable to prepare config push for peer {PeerLabel(state.Peer)}: {exception.Message}");
            return;
        }

        state.ResetConfigPush();
        for (int index = 0; index < chunks.Count; index++)
        {
            state.PendingConfigChunks.Enqueue(new OutboundConfigChunk(chunks.Count, index, chunks[index]));
        }

        _log.Info(
            $"Config push to peer {PeerLabel(state.Peer)} queued: {chunks.Count} chunk(s).");
    }

    private void HandleConfig(long sender, ZPackage package)
    {
        ZNet? net = ZNet.instance;
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (!ReferenceEquals(_active, this) || net == null || routedRpc == null || net.IsServer())
        {
            return;
        }

        ZNetPeer? serverPeer = net.GetServerPeer();
        long serverPeerId = serverPeer?.m_uid ?? 0L;
        if (sender != serverPeerId)
        {
            _log.Warning($"Ignored spoofed {ConfigRpc} from peer uid {sender}.");
            return;
        }

        int totalChunks;
        int index;
        string text;
        try
        {
            totalChunks = package.ReadInt();
            index = package.ReadInt();
            text = package.ReadString();
        }
        catch (Exception exception)
        {
            _log.Warning($"Ignored malformed {ConfigRpc} from the server: {exception.Message}");
            return;
        }

        if (totalChunks <= 0 || totalChunks > MaximumConfigChunks || index < 0 || index >= totalChunks)
        {
            _log.Warning(
                $"Ignored invalid {ConfigRpc} chunk header ({index + 1}/{totalChunks}).");
            return;
        }

        if (Encoding.UTF8.GetByteCount(text) > ConfigSyncSerializer.MaximumChunkTextBytes)
        {
            _log.Warning($"Ignored oversized {ConfigRpc} chunk {index + 1}/{totalChunks}.");
            return;
        }

        ClientConfigBuffer? buffer = _clientConfigBuffer;
        if (buffer == null)
        {
            if (index != 0)
            {
                _log.Warning($"Ignored out-of-sequence {ConfigRpc} chunk {index + 1}/{totalChunks}.");
                return;
            }

            buffer = new ClientConfigBuffer(totalChunks);
            _clientConfigBuffer = buffer;
        }

        if (buffer.TotalChunks != totalChunks)
        {
            _log.Warning("Ignored config chunk whose total does not match the active push.");
            return;
        }

        string? existing = buffer.Chunks[index];
        if (existing != null && !string.Equals(existing, text, StringComparison.Ordinal))
        {
            _log.Warning($"Ignored conflicting duplicate {ConfigRpc} chunk {index + 1}/{totalChunks}.");
            return;
        }

        if (existing == null)
        {
            if (index != buffer.ReceivedChunks)
            {
                _log.Warning($"Ignored out-of-sequence {ConfigRpc} chunk {index + 1}/{totalChunks}.");
                return;
            }

            buffer.Chunks[index] = text;
            buffer.ReceivedChunks++;
        }

        SendAck(routedRpc, sender, index);
        if (buffer.ReceivedChunks != buffer.TotalChunks)
        {
            return;
        }

        _clientConfigBuffer = null;
        var serializedConfig = new StringBuilder();
        foreach (string? chunk in buffer.Chunks)
        {
            serializedConfig.Append(chunk);
        }

        try
        {
            int appliedValues = _settings.ApplyOverlay(serializedConfig.ToString());
            _log.Info(
                $"Server config applied: {appliedValues} value(s) from " +
                $"{buffer.TotalChunks} chunk(s); local cfg unchanged.");
        }
        catch (Exception exception)
        {
            _log.Error($"Server config overlay rejected; local settings remain active: {exception.Message}");
        }
    }

    private void SendAck(ZRoutedRpc routedRpc, long serverPeerId, int index)
    {
        var ack = new ZPackage();
        ack.Write(index);
        try
        {
            routedRpc.InvokeRoutedRPC(serverPeerId, AckRpc, ack);
        }
        catch (Exception exception)
        {
            _log.Warning($"Unable to acknowledge config chunk {index + 1}: {exception.Message}");
        }
    }

    private void HandleAck(long sender, ZPackage package)
    {
        ZNet? net = ZNet.instance;
        if (!ReferenceEquals(_active, this) || net == null || !net.IsServer())
        {
            return;
        }

        int index;
        try
        {
            index = package.ReadInt();
        }
        catch (Exception exception)
        {
            _log.Warning($"Ignored malformed {AckRpc} from peer uid {sender}: {exception.Message}");
            return;
        }

        if (!_peerStates.TryGetValue(sender, out PeerHandshakeState? state) ||
            !state.AwaitingConfigAck ||
            state.AwaitingConfigIndex != index)
        {
            _log.Warning($"Ignored unexpected {AckRpc} for chunk {index + 1} from peer uid {sender}.");
            return;
        }

        state.AwaitingConfigAck = false;
        state.AwaitingConfigIndex = -1;
        state.AcknowledgedConfigChunks++;
        if (state.PendingConfigChunks.Count == 0)
        {
            _log.Info(
                $"Config push to peer {PeerLabel(state.Peer)} complete: " +
                $"{state.AcknowledgedConfigChunks} chunk(s) sent and acknowledged.");
            state.ResetConfigPush();
        }
    }

    private void PumpServer(ZNet net)
    {
        ReconcileServerPeers(net);
        if (!_serverConfig.Enabled)
        {
            return;
        }

        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || !IsRegistered(routedRpc))
        {
            return;
        }

        foreach (PeerHandshakeState state in new List<PeerHandshakeState>(_peerStates.Values))
        {
            if (state.KickIssued || !state.Compatible || state.AwaitingConfigAck ||
                state.PendingConfigChunks.Count == 0)
            {
                continue;
            }

            OutboundConfigChunk chunk = state.PendingConfigChunks.Dequeue();
            var package = new ZPackage();
            package.Write(chunk.TotalChunks);
            package.Write(chunk.Index);
            package.Write(chunk.Text);

            state.AwaitingConfigAck = true;
            state.AwaitingConfigIndex = chunk.Index;
            try
            {
                routedRpc.InvokeRoutedRPC(state.PeerId, ConfigRpc, package);
                _log.Debug(
                    $"Config push to peer {PeerLabel(state.Peer)} sent chunk " +
                    $"{chunk.Index + 1}/{chunk.TotalChunks}.");
            }
            catch (Exception exception)
            {
                state.ResetConfigPush();
                _log.Error(
                    $"Config push to peer {PeerLabel(state.Peer)} failed: {exception.Message}");
            }
        }
    }

    private void ReconcileServerPeers(ZNet net)
    {
        float now = Time.realtimeSinceStartup;
        var connectedPeerIds = new HashSet<long>();
        foreach (ZNetPeer peer in new List<ZNetPeer>(net.GetPeers()))
        {
            if (peer.m_server || !peer.IsReady())
            {
                continue;
            }

            connectedPeerIds.Add(peer.m_uid);
            PeerHandshakeState state = GetOrCreateState(peer, now);
            if (state.KickIssued)
            {
                continue;
            }

            if (state.HelloReceived && !state.Compatible)
            {
                if (IsEnforcementEnabled)
                {
                    KickPeer(
                        net,
                        state,
                        $"incompatible ValheimOne handshake (client v{state.RemoteVersion}, " +
                        $"schema {state.RemoteSchema})");
                }

                continue;
            }

            if (state.HelloReceived)
            {
                continue;
            }

            int graceSeconds = Math.Max(0, _serverConfig.HandshakeGraceSeconds.Value);
            if (now - state.FirstObservedAt < graceSeconds)
            {
                continue;
            }

            if (state.VanillaClassified)
            {
                if (IsEnforcementEnabled)
                {
                    KickPeer(
                        net,
                        state,
                        $"no {HelloRpc} received within {graceSeconds}s (vanilla client)");
                }

                continue;
            }

            if (!_serverConfig.Enabled)
            {
                continue;
            }

            state.VanillaClassified = true;
            string reason = $"no {HelloRpc} received within {graceSeconds}s (vanilla client)";
            if (IsEnforcementEnabled)
            {
                KickPeer(net, state, reason);
            }
            else if (_serverConfig.Enabled)
            {
                _log.Info(
                    $"Peer {PeerLabel(peer)} is vanilla: {reason}; allowed because " +
                    "Server.EnforceMod=false.");
            }
        }

        var disconnectedPeerIds = new List<long>();
        foreach (long peerId in _peerStates.Keys)
        {
            if (!connectedPeerIds.Contains(peerId))
            {
                disconnectedPeerIds.Add(peerId);
            }
        }

        foreach (long peerId in disconnectedPeerIds)
        {
            _peerStates.Remove(peerId);
        }
    }

    private PeerHandshakeState GetOrCreateState(ZNetPeer peer)
    {
        return GetOrCreateState(peer, Time.realtimeSinceStartup);
    }

    private PeerHandshakeState GetOrCreateState(ZNetPeer peer, float observedAt)
    {
        if (_peerStates.TryGetValue(peer.m_uid, out PeerHandshakeState? state) &&
            ReferenceEquals(state.Peer, peer))
        {
            return state;
        }

        state = new PeerHandshakeState(peer, observedAt);
        _peerStates[peer.m_uid] = state;
        return state;
    }

    private bool IsEnforcementEnabled =>
        _serverConfig.Enabled && _serverConfig.EnforceMod.Value;

    private void KickPeer(ZNet net, PeerHandshakeState state, string reason)
    {
        if (state.KickIssued)
        {
            return;
        }

        state.KickIssued = true;
        state.ResetConfigPush();
        _log.Warning($"Peer {PeerLabel(state.Peer)} kicked: {reason}.");
        try
        {
            net.Kick(state.Peer.m_playerName);
        }
        catch (Exception exception)
        {
            _log.Error(
                $"Failed to kick peer {PeerLabel(state.Peer)} after handshake rejection: " +
                exception.Message);
        }
    }

    private void ResetNetworkState()
    {
        if (_settings.ClearOverlay())
        {
            _log.Info("Server config overlay cleared; local settings restored.");
        }

        _peerStates.Clear();
        _clientConfigBuffer = null;
        _clientHelloFailureLogged = false;
        _clientHelloSent = false;
    }

    private static string PeerLabel(ZNetPeer peer)
    {
        return string.IsNullOrWhiteSpace(peer.m_playerName)
            ? $"uid {peer.m_uid}"
            : $"'{peer.m_playerName}' (uid {peer.m_uid})";
    }

    private sealed class PeerHandshakeState
    {
        public PeerHandshakeState(ZNetPeer peer, float firstObservedAt)
        {
            Peer = peer;
            PeerId = peer.m_uid;
            FirstObservedAt = firstObservedAt;
        }

        public ZNetPeer Peer { get; }

        public long PeerId { get; }

        public float FirstObservedAt { get; }

        public bool HelloReceived { get; set; }

        public bool Compatible { get; set; }

        public bool VanillaClassified { get; set; }

        public bool KickIssued { get; set; }

        public string RemoteVersion { get; set; } = "unknown";

        public int RemoteSchema { get; set; } = -1;

        public string RemoteConfigHash { get; set; } = string.Empty;

        public Queue<OutboundConfigChunk> PendingConfigChunks { get; } =
            new Queue<OutboundConfigChunk>();

        public bool AwaitingConfigAck { get; set; }

        public int AwaitingConfigIndex { get; set; } = -1;

        public int AcknowledgedConfigChunks { get; set; }

        public void ResetConfigPush()
        {
            PendingConfigChunks.Clear();
            AwaitingConfigAck = false;
            AwaitingConfigIndex = -1;
            AcknowledgedConfigChunks = 0;
        }
    }

    private sealed class ClientConfigBuffer
    {
        public ClientConfigBuffer(int totalChunks)
        {
            TotalChunks = totalChunks;
            Chunks = new string?[totalChunks];
        }

        public int TotalChunks { get; }

        public string?[] Chunks { get; }

        public int ReceivedChunks { get; set; }
    }

    private readonly struct OutboundConfigChunk
    {
        public OutboundConfigChunk(int totalChunks, int index, string text)
        {
            TotalChunks = totalChunks;
            Index = index;
            Text = text;
        }

        public int TotalChunks { get; }

        public int Index { get; }

        public string Text { get; }
    }
}
