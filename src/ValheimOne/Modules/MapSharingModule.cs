using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.Networking;

namespace ValheimOne.Modules;

public sealed class MapSharingModule : IFeatureModule, IVersionHandshakeExtension
{
    private const string MapRpc = "VO_Map";
    private const int MaximumRangesPerChunk = 330;
    private const int MaximumChunksPerTransfer = 16384;
    private const int MaximumTextureSize = 8192;
    private const float PersistenceIntervalSeconds = 300f;
    private const int PersistenceMagic = 0x564F4D50;
    private const int PersistenceVersion = 1;

    private static readonly AccessTools.FieldRef<Minimap, bool[]> ExploredField =
        AccessTools.FieldRefAccess<Minimap, bool[]>("m_explored");
    private static readonly AccessTools.FieldRef<Minimap, Texture2D> FogTextureField =
        AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");

    [ThreadStatic]
    private static bool s_forcingPositionSharing;

    private static MapSharingModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _forcePositionSharing;
    private readonly ConfigEntryBool _sharedExploration;
    private readonly ConfigEntryInt _explorationSyncSeconds;
    private readonly ModLogger _log;
    private readonly string _storageDirectory;
    private readonly HashSet<long> _compatiblePeers = new HashSet<long>();
    private readonly Dictionary<long, AckGatedChunkQueue<OutboundMapChunk>> _serverQueues =
        new Dictionary<long, AckGatedChunkQueue<OutboundMapChunk>>();
    private readonly Dictionary<long, InboundMapTransfer> _serverInbound =
        new Dictionary<long, InboundMapTransfer>();
    private readonly AckGatedChunkQueue<OutboundMapChunk> _clientQueue =
        new AckGatedChunkQueue<OutboundMapChunk>();

    private InboundMapTransfer? _clientInbound;
    private Minimap? _clientMinimap;
    private bool[]? _clientSent;
    private bool[]? _serverUnion;
    private bool[]? _serverPendingDelta;
    private long _serverWorldUid;
    private bool _serverWorldKnown;
    private bool _serverDirty;
    private bool _sharingWasActive;
    private int _nextTransferId = 1;
    private float _nextClientSyncAt;
    private float _nextServerSyncAt;
    private float _nextPersistenceAt;

    public MapSharingModule(FeatureRegistry registry, ModLogger log)
    {
        _feature = registry.Register(Name, Section, Classification);
        _forcePositionSharing = _feature.Bool(
            "ForcePositionSharing",
            defaultValue: false,
            "Prevent modded clients from disabling public map position sharing.");
        _sharedExploration = _feature.Bool(
            "SharedExploration",
            defaultValue: false,
            "Merge modded players' explored minimap pixels on the server and sync the union to modded clients.");
        _explorationSyncSeconds = _feature.Int(
            "ExplorationSyncSeconds",
            60,
            "Absolute cadence in seconds for incremental exploration sync after the join exchange; values below 10 use 10.");
        _log = log;
        _storageDirectory = Path.Combine(Paths.ConfigPath, "ValheimOne", "MapSharing");
        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Map sharing";

    public string Section => "MapSharing";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    private bool SharingActive => IsEnabled && _sharedExploration.Value;

    private int SyncSeconds => Math.Max(10, Math.Abs(_explorationSyncSeconds.Value));

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var setPublicPosition = AccessTools.Method(
            typeof(ZNet),
            nameof(ZNet.SetPublicReferencePosition),
            new[] { typeof(bool) })
            ?? throw new MissingMethodException(nameof(ZNet), nameof(ZNet.SetPublicReferencePosition));
        harmony.Patch(
            setPublicPosition,
            postfix: new HarmonyMethod(typeof(MapSharingModule), nameof(SetPublicPositionPostfix)));

        var loadMapData = AccessTools.Method(typeof(Minimap), "LoadMapData", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Minimap), "LoadMapData");
        harmony.Patch(
            loadMapData,
            postfix: new HarmonyMethod(typeof(MapSharingModule), nameof(LoadMapDataPostfix)));

        var minimapOnDestroy = AccessTools.Method(typeof(Minimap), "OnDestroy", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Minimap), "OnDestroy");
        harmony.Patch(
            minimapOnDestroy,
            postfix: new HarmonyMethod(typeof(MapSharingModule), nameof(MinimapOnDestroyPostfix)));
    }

    void IVersionHandshakeExtension.RegisterRpcHandlers(ZRoutedRpc routedRpc)
    {
        routedRpc.Register<ZPackage>(MapRpc, HandleMap);
    }

    void IVersionHandshakeExtension.OnPeerCompatible(long peerId)
    {
        _compatiblePeers.Add(peerId);
        GetOrCreateServerQueue(peerId);
        if (SharingActive && _serverUnion != null)
        {
            QueueTransfer(GetOrCreateServerQueue(peerId), _serverUnion, null, _serverUnionSize: GetServerSize());
        }
    }

    void IVersionHandshakeExtension.Pump(
        ZNet net,
        ZRoutedRpc routedRpc,
        IReadOnlyCollection<long> compatiblePeerIds)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (net.IsServer())
        {
            PumpServer(net, routedRpc, compatiblePeerIds);
        }
        else
        {
            PumpClient(net, routedRpc);
        }
    }

    bool IVersionHandshakeExtension.TryHandleAcknowledgement(
        long sender,
        string channel,
        int index)
    {
        if (!IsEnabled || !string.Equals(channel, MapRpc, StringComparison.Ordinal))
        {
            return false;
        }

        ZNet? net = ZNet.instance;
        if (net == null)
        {
            return false;
        }

        if (net.IsServer())
        {
            return _serverQueues.TryGetValue(sender, out AckGatedChunkQueue<OutboundMapChunk>? queue) &&
                queue.TryAcknowledge(index);
        }

        ZNetPeer? serverPeer = net.GetServerPeer();
        return serverPeer != null && sender == serverPeer.m_uid && _clientQueue.TryAcknowledge(index);
    }

    void IVersionHandshakeExtension.ResetNetworkState() => ResetNetworkState();

    void IVersionHandshakeExtension.Shutdown()
    {
        SaveServerUnion();
        ResetNetworkState();
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
    }

    private static void SetPublicPositionPostfix(ZNet __instance, bool __0)
    {
        MapSharingModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!active._forcePositionSharing.Value || __0 || s_forcingPositionSharing ||
            __instance.IsDedicated())
        {
            return;
        }

        try
        {
            s_forcingPositionSharing = true;
            __instance.SetPublicReferencePosition(true);
        }
        finally
        {
            s_forcingPositionSharing = false;
        }
    }

    private static void LoadMapDataPostfix(Minimap __instance)
    {
        MapSharingModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.BeginClientMap(__instance);
    }

    private static void MinimapOnDestroyPostfix(Minimap __instance)
    {
        MapSharingModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (ReferenceEquals(active._clientMinimap, __instance))
        {
            active.ResetClientState();
        }
    }

    private void OnEffectiveValuesChanged()
    {
        bool sharingActive = SharingActive;
        if (IsEnabled && _forcePositionSharing.Value)
        {
            ZNet? net = ZNet.instance;
            if (net != null && !net.IsDedicated() && !net.IsReferencePositionPublic())
            {
                net.SetPublicReferencePosition(true);
            }
        }

        if (!sharingActive)
        {
            ResetTransferState();
        }
        else if (!_sharingWasActive)
        {
            Minimap? minimap = _clientMinimap ?? Minimap.instance;
            if (minimap != null)
            {
                BeginClientMap(minimap);
            }
        }

        _sharingWasActive = sharingActive;
    }

    private void BeginClientMap(Minimap minimap)
    {
        _clientMinimap = minimap;
        bool[] explored = ExploredField(minimap);
        int size = minimap.m_textureSize;
        if (!IsValidDimensions(size, explored.Length) || !SharingActive)
        {
            return;
        }

        _clientQueue.Reset();
        _clientInbound = null;
        _clientSent = new bool[explored.Length];
        _nextClientSyncAt = Time.realtimeSinceStartup + SyncSeconds;

        ZNet? net = ZNet.instance;
        if (net != null && net.IsServer())
        {
            EnsureServerWorld(net, size);
            MergeIntoServer(explored, size);
            Array.Copy(explored, _clientSent, explored.Length);
            return;
        }

        QueueTransfer(_clientQueue, explored, _clientSent, size);
    }

    private void PumpClient(ZNet net, ZRoutedRpc routedRpc)
    {
        if (!SharingActive)
        {
            return;
        }

        Minimap? minimap = _clientMinimap;
        bool[]? sent = _clientSent;
        ZNetPeer? serverPeer = net.GetServerPeer();
        if (minimap == null || sent == null || serverPeer == null || !serverPeer.IsReady())
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now >= _nextClientSyncAt && _clientQueue.IsIdle)
        {
            bool[] explored = ExploredField(minimap);
            if (IsValidDimensions(minimap.m_textureSize, explored.Length) && sent.Length == explored.Length)
            {
                QueueTransfer(_clientQueue, explored, sent, minimap.m_textureSize, omitEmpty: true);
            }

            _nextClientSyncAt = now + SyncSeconds;
        }

        SendNextChunk(routedRpc, serverPeer.m_uid, _clientQueue);
    }

    private void PumpServer(
        ZNet net,
        ZRoutedRpc routedRpc,
        IReadOnlyCollection<long> compatiblePeerIds)
    {
        ReconcileCompatiblePeers(compatiblePeerIds);
        if (!SharingActive)
        {
            return;
        }

        int knownSize = GetServerSize();
        EnsureServerWorld(net, knownSize);

        float now = Time.realtimeSinceStartup;
        if (_clientMinimap != null && _clientSent != null && now >= _nextClientSyncAt)
        {
            bool[] localExplored = ExploredField(_clientMinimap);
            if (IsValidDimensions(_clientMinimap.m_textureSize, localExplored.Length) &&
                localExplored.Length == _clientSent.Length)
            {
                MergeNewLocalPixels(localExplored, _clientSent, _clientMinimap.m_textureSize);
            }

            _nextClientSyncAt = now + SyncSeconds;
        }

        if (now >= _nextServerSyncAt)
        {
            BroadcastPendingDelta();
            _nextServerSyncAt = now + SyncSeconds;
        }

        if (_serverDirty && now >= _nextPersistenceAt)
        {
            SaveServerUnion();
            _nextPersistenceAt = now + PersistenceIntervalSeconds;
        }

        foreach (long peerId in _compatiblePeers)
        {
            if (_serverQueues.TryGetValue(peerId, out AckGatedChunkQueue<OutboundMapChunk>? queue))
            {
                SendNextChunk(routedRpc, peerId, queue);
            }
        }
    }

    private void HandleMap(long sender, ZPackage package)
    {
        if (!SharingActive)
        {
            return;
        }

        ZNet? net = ZNet.instance;
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (net == null || routedRpc == null)
        {
            return;
        }

        if (net.IsServer())
        {
            if (!_compatiblePeers.Contains(sender))
            {
                _log.Warning($"Ignored {MapRpc} from unhandshaken peer uid {sender}.");
                return;
            }

            HandleIncomingChunk(sender, package, serverSide: true);
        }
        else
        {
            ZNetPeer? serverPeer = net.GetServerPeer();
            if (serverPeer == null || sender != serverPeer.m_uid)
            {
                _log.Warning($"Ignored spoofed {MapRpc} from peer uid {sender}.");
                return;
            }

            HandleIncomingChunk(sender, package, serverSide: false);
        }
    }

    private void HandleIncomingChunk(long sender, ZPackage package, bool serverSide)
    {
        int transferId;
        int totalChunks;
        int index;
        int textureSize;
        int rangeCount;
        try
        {
            transferId = package.ReadInt();
            totalChunks = package.ReadInt();
            index = package.ReadInt();
            textureSize = package.ReadInt();
            rangeCount = package.ReadInt();
        }
        catch (Exception exception)
        {
            _log.Warning($"Ignored malformed {MapRpc} header from peer uid {sender}: {exception.Message}");
            return;
        }

        if (transferId <= 0 || totalChunks <= 0 || totalChunks > MaximumChunksPerTransfer ||
            index < 0 || index >= totalChunks || rangeCount < 0 ||
            rangeCount > MaximumRangesPerChunk || textureSize <= 0 ||
            textureSize > MaximumTextureSize)
        {
            _log.Warning($"Ignored invalid {MapRpc} chunk header from peer uid {sender}.");
            return;
        }

        InboundMapTransfer? inbound = serverSide
            ? GetServerInbound(sender)
            : _clientInbound;
        if (inbound == null || inbound.TransferId != transferId)
        {
            if (index != 0)
            {
                _log.Warning($"Ignored out-of-sequence {MapRpc} chunk {index + 1}/{totalChunks}.");
                return;
            }

            inbound = new InboundMapTransfer(transferId, totalChunks, textureSize);
            if (serverSide)
            {
                _serverInbound[sender] = inbound;
            }
            else
            {
                _clientInbound = inbound;
            }
        }

        if (inbound.TotalChunks != totalChunks || inbound.TextureSize != textureSize ||
            inbound.NextIndex != index)
        {
            _log.Warning($"Ignored inconsistent {MapRpc} chunk {index + 1}/{totalChunks}.");
            return;
        }

        var ranges = new ExploredMapRange[rangeCount];
        try
        {
            for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
            {
                int startX = package.ReadInt();
                int endX = package.ReadInt();
                int y = package.ReadInt();
                if (startX < 0 || endX < startX || endX >= textureSize || y < 0 || y >= textureSize)
                {
                    _log.Warning($"Ignored invalid explored range in {MapRpc} from peer uid {sender}.");
                    return;
                }

                ranges[rangeIndex] = new ExploredMapRange(startX, endX, y);
            }
        }
        catch (Exception exception)
        {
            _log.Warning($"Ignored malformed {MapRpc} ranges from peer uid {sender}: {exception.Message}");
            return;
        }

        if (serverSide)
        {
            if (!ApplyServerRanges(textureSize, ranges))
            {
                return;
            }
        }
        else if (!ApplyClientRanges(textureSize, ranges))
        {
            return;
        }

        inbound.NextIndex++;
        SendAcknowledgement(sender, index);
        if (inbound.NextIndex != inbound.TotalChunks)
        {
            return;
        }

        if (serverSide)
        {
            _serverInbound.Remove(sender);
            if (_serverUnion != null)
            {
                QueueTransfer(GetOrCreateServerQueue(sender), _serverUnion, null, GetServerSize());
            }
        }
        else
        {
            _clientInbound = null;
            RefreshClientFog();
        }
    }

    private bool ApplyServerRanges(int textureSize, IReadOnlyList<ExploredMapRange> ranges)
    {
        ZNet? net = ZNet.instance;
        if (net == null || !net.IsServer())
        {
            return false;
        }

        EnsureServerWorld(net, textureSize);
        if (_serverUnion == null || _serverPendingDelta == null || GetServerSize() != textureSize)
        {
            _log.Warning(
                $"Ignored {MapRpc} with map size {textureSize}; server union size is {GetServerSize()}.");
            return false;
        }

        bool changed = false;
        foreach (ExploredMapRange range in ranges)
        {
            int row = range.Y * textureSize;
            for (int x = range.StartX; x <= range.EndX; x++)
            {
                int pixel = row + x;
                if (_serverUnion[pixel])
                {
                    continue;
                }

                _serverUnion[pixel] = true;
                _serverPendingDelta[pixel] = true;
                changed = true;
            }
        }

        if (changed)
        {
            _serverDirty = true;
            if (_clientMinimap != null && _clientMinimap.m_textureSize == textureSize)
            {
                ApplyRangesToClientBitmap(ranges, textureSize);
                RefreshClientFog();
            }
        }

        return true;
    }

    private bool ApplyClientRanges(int textureSize, IReadOnlyList<ExploredMapRange> ranges)
    {
        Minimap? minimap = _clientMinimap;
        if (minimap == null || minimap.m_textureSize != textureSize)
        {
            _log.Warning($"Ignored {MapRpc} map size {textureSize}; local minimap does not match.");
            return false;
        }

        ApplyRangesToClientBitmap(ranges, textureSize);
        return true;
    }

    private void ApplyRangesToClientBitmap(IReadOnlyList<ExploredMapRange> ranges, int textureSize)
    {
        Minimap? minimap = _clientMinimap;
        if (minimap == null)
        {
            return;
        }

        bool[] explored = ExploredField(minimap);
        bool[]? sent = _clientSent;
        foreach (ExploredMapRange range in ranges)
        {
            int row = range.Y * textureSize;
            for (int x = range.StartX; x <= range.EndX; x++)
            {
                int pixel = row + x;
                explored[pixel] = true;
                if (sent != null && sent.Length == explored.Length)
                {
                    sent[pixel] = true;
                }
            }
        }
    }

    private void RefreshClientFog()
    {
        Minimap? minimap = _clientMinimap;
        if (minimap == null)
        {
            return;
        }

        bool[] explored = ExploredField(minimap);
        Texture2D fogTexture = FogTextureField(minimap);
        Color32[] pixels = fogTexture.GetPixels32();
        if (pixels.Length != explored.Length)
        {
            _log.Warning("Unable to refresh shared minimap fog because texture dimensions changed.");
            return;
        }

        for (int index = 0; index < explored.Length; index++)
        {
            if (explored[index])
            {
                pixels[index].r = 0;
            }
        }

        fogTexture.SetPixels32(pixels);
        fogTexture.Apply();
    }

    private void SendAcknowledgement(long recipient, int index)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            return;
        }

        var ack = new ZPackage();
        ack.Write(MapRpc);
        ack.Write(index);
        routedRpc.InvokeRoutedRPC(recipient, VersionHandshake.AckRpc, ack);
    }

    private void SendNextChunk(
        ZRoutedRpc routedRpc,
        long recipient,
        AckGatedChunkQueue<OutboundMapChunk> queue)
    {
        if (!queue.TryStartNext(out OutboundMapChunk? nullableChunk) || nullableChunk == null)
        {
            return;
        }

        OutboundMapChunk chunk = nullableChunk;
        var package = new ZPackage();
        package.Write(chunk.TransferId);
        package.Write(chunk.TotalChunks);
        package.Write(chunk.Index);
        package.Write(chunk.TextureSize);
        package.Write(chunk.Ranges.Length);
        foreach (ExploredMapRange range in chunk.Ranges)
        {
            package.Write(range.StartX);
            package.Write(range.EndX);
            package.Write(range.Y);
        }

        try
        {
            routedRpc.InvokeRoutedRPC(recipient, MapRpc, package);
        }
        catch (Exception exception)
        {
            queue.Reset();
            _log.Warning($"Unable to send {MapRpc} to peer uid {recipient}: {exception.Message}");
        }
    }

    private void QueueTransfer(
        AckGatedChunkQueue<OutboundMapChunk> queue,
        bool[] explored,
        bool[]? alreadySent,
        int _serverUnionSize,
        bool omitEmpty = false)
    {
        List<ExploredMapRange[]> chunks = EncodeRanges(explored, alreadySent, _serverUnionSize);
        if (chunks.Count == 0)
        {
            if (omitEmpty)
            {
                return;
            }

            chunks.Add(Array.Empty<ExploredMapRange>());
        }

        int transferId = NextTransferId();
        for (int index = 0; index < chunks.Count; index++)
        {
            queue.Enqueue(new OutboundMapChunk(
                transferId,
                chunks.Count,
                index,
                _serverUnionSize,
                chunks[index]));
        }
    }

    private static List<ExploredMapRange[]> EncodeRanges(
        bool[] explored,
        bool[]? alreadySent,
        int textureSize)
    {
        var result = new List<ExploredMapRange[]>();
        var current = new List<ExploredMapRange>(MaximumRangesPerChunk);
        for (int y = 0; y < textureSize; y++)
        {
            int row = y * textureSize;
            int x = 0;
            while (x < textureSize)
            {
                while (x < textureSize &&
                    (!explored[row + x] || (alreadySent != null && alreadySent[row + x])))
                {
                    x++;
                }

                if (x >= textureSize)
                {
                    break;
                }

                int startX = x;
                while (x + 1 < textureSize && explored[row + x + 1] &&
                    (alreadySent == null || !alreadySent[row + x + 1]))
                {
                    x++;
                }

                int endX = x;
                current.Add(new ExploredMapRange(startX, endX, y));
                if (alreadySent != null)
                {
                    for (int markX = startX; markX <= endX; markX++)
                    {
                        alreadySent[row + markX] = true;
                    }
                }

                if (current.Count == MaximumRangesPerChunk)
                {
                    result.Add(current.ToArray());
                    current.Clear();
                }

                x++;
            }
        }

        if (current.Count != 0)
        {
            result.Add(current.ToArray());
        }

        return result;
    }

    private void BroadcastPendingDelta()
    {
        if (_serverPendingDelta == null || _serverUnion == null)
        {
            return;
        }

        bool any = false;
        for (int index = 0; index < _serverPendingDelta.Length; index++)
        {
            if (_serverPendingDelta[index])
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        int size = GetServerSize();
        foreach (long peerId in _compatiblePeers)
        {
            QueueTransfer(GetOrCreateServerQueue(peerId), _serverPendingDelta, null, size);
        }

        Array.Clear(_serverPendingDelta, 0, _serverPendingDelta.Length);
    }

    private void MergeNewLocalPixels(bool[] explored, bool[] sent, int textureSize)
    {
        if (_serverUnion == null || _serverPendingDelta == null || GetServerSize() != textureSize)
        {
            return;
        }

        bool changed = false;
        for (int index = 0; index < explored.Length; index++)
        {
            if (!explored[index] || sent[index])
            {
                continue;
            }

            sent[index] = true;
            if (!_serverUnion[index])
            {
                _serverUnion[index] = true;
                _serverPendingDelta[index] = true;
                changed = true;
            }
        }

        _serverDirty |= changed;
    }

    private void MergeIntoServer(bool[] explored, int textureSize)
    {
        if (_serverUnion == null || _serverPendingDelta == null || GetServerSize() != textureSize)
        {
            return;
        }

        bool changed = false;
        for (int index = 0; index < explored.Length; index++)
        {
            if (explored[index] && !_serverUnion[index])
            {
                _serverUnion[index] = true;
                _serverPendingDelta[index] = true;
                changed = true;
            }
        }

        _serverDirty |= changed;
    }

    private void EnsureServerWorld(ZNet net, int requestedSize)
    {
        long worldUid = net.GetWorldUID();
        if (!_serverWorldKnown || _serverWorldUid != worldUid)
        {
            SaveServerUnion();
            _serverWorldUid = worldUid;
            _serverWorldKnown = true;
            _serverUnion = null;
            _serverPendingDelta = null;
            _serverDirty = false;
            _nextPersistenceAt = Time.realtimeSinceStartup + PersistenceIntervalSeconds;
        }

        if (_serverUnion != null || requestedSize <= 0)
        {
            return;
        }

        int length;
        try
        {
            length = checked(requestedSize * requestedSize);
        }
        catch (OverflowException)
        {
            return;
        }

        _serverUnion = new bool[length];
        _serverPendingDelta = new bool[length];
        LoadServerUnion(requestedSize);
    }

    private void LoadServerUnion(int textureSize)
    {
        string path = GetPersistencePath();
        if (!File.Exists(path) || _serverUnion == null)
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != PersistenceMagic || reader.ReadInt32() != PersistenceVersion ||
                reader.ReadInt32() != textureSize)
            {
                _log.Warning($"Ignored incompatible shared exploration store: {path}");
                return;
            }

            int byteCount = reader.ReadInt32();
            int expectedBytes = (_serverUnion.Length + 7) / 8;
            if (byteCount != expectedBytes)
            {
                _log.Warning($"Ignored malformed shared exploration store: {path}");
                return;
            }

            byte[] packed = reader.ReadBytes(byteCount);
            if (packed.Length != byteCount)
            {
                _log.Warning($"Ignored truncated shared exploration store: {path}");
                return;
            }

            for (int index = 0; index < _serverUnion.Length; index++)
            {
                _serverUnion[index] = (packed[index >> 3] & (1 << (index & 7))) != 0;
            }

            _log.Info($"Loaded shared exploration for world {_serverWorldUid}.");
        }
        catch (Exception exception)
        {
            _log.Warning($"Unable to load shared exploration store: {exception.Message}");
        }
    }

    private void SaveServerUnion()
    {
        if (!_serverDirty || !_serverWorldKnown || _serverUnion == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_storageDirectory);
            string path = GetPersistencePath();
            string temporaryPath = path + ".tmp";
            byte[] packed = new byte[(_serverUnion.Length + 7) / 8];
            for (int index = 0; index < _serverUnion.Length; index++)
            {
                if (_serverUnion[index])
                {
                    packed[index >> 3] |= (byte)(1 << (index & 7));
                }
            }

            using (var stream = File.Create(temporaryPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PersistenceMagic);
                writer.Write(PersistenceVersion);
                writer.Write(GetServerSize());
                writer.Write(packed.Length);
                writer.Write(packed);
            }

            File.Copy(temporaryPath, path, overwrite: true);
            File.Delete(temporaryPath);
            _serverDirty = false;
            _log.Debug($"Saved shared exploration for world {_serverWorldUid}.");
        }
        catch (Exception exception)
        {
            _log.Warning($"Unable to save shared exploration store: {exception.Message}");
        }
    }

    private string GetPersistencePath() =>
        Path.Combine(_storageDirectory, _serverWorldUid + "_mapSync.bin");

    private int GetServerSize()
    {
        if (_serverUnion == null)
        {
            return 0;
        }

        return (int)Math.Sqrt(_serverUnion.Length);
    }

    private void ReconcileCompatiblePeers(IReadOnlyCollection<long> compatiblePeerIds)
    {
        var connected = new HashSet<long>(compatiblePeerIds);
        _compatiblePeers.RemoveWhere(peerId => !connected.Contains(peerId));

        var disconnected = new List<long>();
        foreach (long peerId in _serverQueues.Keys)
        {
            if (!connected.Contains(peerId))
            {
                disconnected.Add(peerId);
            }
        }

        foreach (long peerId in disconnected)
        {
            _serverQueues.Remove(peerId);
            _serverInbound.Remove(peerId);
        }
    }

    private InboundMapTransfer? GetServerInbound(long sender)
    {
        _serverInbound.TryGetValue(sender, out InboundMapTransfer? inbound);
        return inbound;
    }

    private AckGatedChunkQueue<OutboundMapChunk> GetOrCreateServerQueue(long peerId)
    {
        if (!_serverQueues.TryGetValue(peerId, out AckGatedChunkQueue<OutboundMapChunk>? queue))
        {
            queue = new AckGatedChunkQueue<OutboundMapChunk>();
            _serverQueues[peerId] = queue;
        }

        return queue;
    }

    private int NextTransferId()
    {
        if (_nextTransferId == int.MaxValue)
        {
            _nextTransferId = 1;
        }

        return _nextTransferId++;
    }

    private static bool IsValidDimensions(int size, int length)
    {
        if (size <= 0 || size > MaximumTextureSize)
        {
            return false;
        }

        try
        {
            return checked(size * size) == length;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void ResetTransferState()
    {
        _clientQueue.Reset();
        _clientInbound = null;
        _serverInbound.Clear();
        foreach (AckGatedChunkQueue<OutboundMapChunk> queue in _serverQueues.Values)
        {
            queue.Reset();
        }
    }

    private void ResetClientState()
    {
        _clientMinimap = null;
        _clientSent = null;
        _clientQueue.Reset();
        _clientInbound = null;
    }

    private void ResetNetworkState()
    {
        SaveServerUnion();
        ResetClientState();
        _compatiblePeers.Clear();
        _serverQueues.Clear();
        _serverInbound.Clear();
        _serverUnion = null;
        _serverPendingDelta = null;
        _serverWorldKnown = false;
        _serverDirty = false;
        _sharingWasActive = false;
    }

    private sealed class InboundMapTransfer
    {
        public InboundMapTransfer(int transferId, int totalChunks, int textureSize)
        {
            TransferId = transferId;
            TotalChunks = totalChunks;
            TextureSize = textureSize;
        }

        public int TransferId { get; }

        public int TotalChunks { get; }

        public int TextureSize { get; }

        public int NextIndex { get; set; }
    }

    private sealed class OutboundMapChunk : IAcknowledgedChunk
    {
        public OutboundMapChunk(
            int transferId,
            int totalChunks,
            int index,
            int textureSize,
            ExploredMapRange[] ranges)
        {
            TransferId = transferId;
            TotalChunks = totalChunks;
            Index = index;
            TextureSize = textureSize;
            Ranges = ranges;
        }

        public int TransferId { get; }

        public int TotalChunks { get; }

        public int Index { get; }

        public int TextureSize { get; }

        public ExploredMapRange[] Ranges { get; }
    }
}
