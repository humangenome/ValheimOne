using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;
using ValheimOne.Modules;
using ValheimOne.Networking;

namespace ValheimOne.RuntimeRegression;

// Test-only plugin. Never packaged with ValheimOne. Uses real game components and
// inventory serialization; the deterministic two-replica transport lets us deliver
// replies before data, duplicate replies, and ownership changes in controlled order.
[BepInPlugin("com.humangenome.valheimone.regression", "ValheimOne runtime regression", "1.0.0")]
[BepInDependency(ValheimOnePlugin.PluginGuid)]
public sealed class RuntimeRegression : BaseUnityPlugin
{
    private const long RemotePeer = 8877665511L;
    private static readonly HashSet<ZNetView> CapturedViews = new();
    private static readonly Queue<(long Target, string Name, byte[] Data)> Packets = new();
    private static bool _denyWard;
    private readonly List<GameObject> _objects = new();
    private Harmony? _harmony;
    private int _assertions;
    private bool _ran;
    private string _root = "";
    // The live event-stream check needs the running map, which starts on the main thread after these tests, so it
    // runs on its own thread and the result file is written once it is done.
    private readonly List<(bool Ok, string Name)> _liveResults = new();
    private Thread? _liveCheck;
    private volatile bool _liveDone;
    private bool _resultWritten;
    private float _liveDeadline;

    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("VALHEIMONE_RUNTIME_REGRESSION") != "1")
        {
            enabled = false;
            return;
        }
        _root = Path.Combine(Paths.ConfigPath, "runtime-regression");
        Directory.CreateDirectory(_root);
        _harmony = new Harmony("com.humangenome.valheimone.regression");
        _harmony.Patch(AccessTools.Method(typeof(ZNetView), "InvokeRPC",
            new[] { typeof(long), typeof(string), typeof(object[]) }),
            prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(CaptureRpc)));
        _harmony.Patch(AccessTools.Method(typeof(PrivateArea), "CheckAccess",
            new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(CheckWard)));
    }

    private void Update()
    {
        if (_ran)
        {
            PollLiveChecks();
            return;
        }

        if (ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZNetScene.instance == null || ObjectDB.instance == null ||
            ObjectDB.instance.GetItemPrefab("Wood") == null || WorldGenerator.instance == null) return;
        _ran = true;
        Player? previous = Player.m_localPlayer;
        try
        {
            TestCrossplayLobbyCollision();
            TestBitmapStorage();
            TestWebFogCartography();
            TestMapExchange();
            TestChestTransfer();
            TestStations();
            TestProductionSettings();
            TestWeatherDamage();
            TestFoodDegradation();
            TestRegionLabels();
            TestMapStartSettings();
            TestValheimOneZeroData();
            TestDropsChestLoot();
            TestNetworkSendLimit();
            TestWorldData();
            TestEventStreamSlots();
            _liveDeadline = Time.realtimeSinceStartup + 200f;
            _liveCheck = new Thread(RunLiveEventStreamChecks) { IsBackground = true, Name = "vo-regression-events" };
            _liveCheck.Start();
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
        }
        finally
        {
            Set(typeof(Player), "m_localPlayer", previous);
            _harmony?.UnpatchSelf();
            foreach (GameObject obj in _objects)
            {
                if (obj == null) continue;
                ZNetView? view = obj.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && ZNetScene.instance != null)
                {
                    // A plain Unity Destroy leaves registered chest views in ZNetScene,
                    // which then dereferences a dead component during its next update.
                    ZNetScene.instance.Destroy(obj);
                }
                else
                {
                    Destroy(obj);
                }
            }
            CapturedViews.Clear();
        }
    }

    private void WriteFailure(Exception exception)
    {
        if (_resultWritten) return;
        _resultWritten = true;
        Logger.LogError("RUNTIME REGRESSION FAIL " + exception);
        File.WriteAllText(Path.Combine(_root, "result.txt"), "FAIL " + exception + "\n");
    }

    private void PollLiveChecks()
    {
        if (_resultWritten || _liveCheck == null) return;
        if (!_liveDone)
        {
            if (Time.realtimeSinceStartup > _liveDeadline)
                WriteFailure(new TimeoutException("the live event-stream check did not finish"));
            return;
        }

        try
        {
            lock (_liveResults)
            {
                Check(_liveResults.Count > 0, "the live event-stream check reported");
                foreach ((bool ok, string name) in _liveResults) Check(ok, name);
            }

            _resultWritten = true;
            string result = $"RUNTIME REGRESSION PASS assertions={_assertions} game={(global::Version.GetVersionString())}";
            Logger.LogInfo(result);
            File.WriteAllText(Path.Combine(_root, "result.txt"), result + "\n");
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
        }
    }

    private static bool CaptureRpc(ZNetView __instance, long __0, string __1, object[] __2)
    {
        if (!CapturedViews.Contains(__instance) || !__1.StartsWith("VO_CraftChest", StringComparison.Ordinal))
            return true;
        Packets.Enqueue((__0, __1, ((ZPackage)__2[0]).GetArray()));
        return false;
    }

    private static bool CheckWard(ref bool __result)
    {
        if (!_denyWard) return true;
        __result = false;
        return false;
    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _assertions++;
        Logger.LogInfo("REGRESSION " + name);
    }

    private static ZPlayFabMatchmaking? _lobbyFixture;
    private static int _regenerated;
    private static int _activated;

    private static bool CaptureRegeneration(ZPlayFabMatchmaking __instance)
    {
        if (!ReferenceEquals(__instance, _lobbyFixture)) return true;
        _regenerated++;
        return false;
    }

    private static bool CaptureActivation(ZPlayFabMatchmaking __instance)
    {
        if (!ReferenceEquals(__instance, _lobbyFixture)) return true;
        _activated++;
        return false;
    }

    private static object? ReadMember(object instance, string name) =>
        AccessTools.Field(instance.GetType(), name)?.GetValue(instance) ??
        AccessTools.Property(instance.GetType(), name)?.GetValue(instance);

    private static void WriteMember(object instance, string name, object? value)
    {
        FieldInfo? field = AccessTools.Field(instance.GetType(), name);
        if (field != null) field.SetValue(instance, value);
        else AccessTools.Property(instance.GetType(), name).SetValue(instance, value);
    }

    private void TestCrossplayLobbyCollision()
    {
        MethodInfo callback = AccessTools.Method(typeof(ZPlayFabMatchmaking), "OnCheckJoinCodeSuccess");
        MethodInfo prefix = AccessTools.Method(typeof(CrossplayLobbyCompatibility), "CheckJoinCodePrefix");
        var pluginHarmony = new Harmony(ValheimOnePlugin.PluginGuid);
        _lobbyFixture = (ZPlayFabMatchmaking)FormatterServices.GetUninitializedObject(typeof(ZPlayFabMatchmaking));
        FieldInfo serverDataField = AccessTools.Field(typeof(ZPlayFabMatchmaking), "m_serverData");
        object serverData = Activator.CreateInstance(serverDataField.FieldType, true);
        WriteMember(serverData, "serverName", "Regression lobby");
        serverDataField.SetValue(_lobbyFixture, serverData);
        // The join code must come from the OS random source, not the world-seeded Unity generator.
        MethodInfo generate = AccessTools.Method(typeof(ZPlayFabMatchmaking), "GenerateJoinCode");
        PropertyInfo joinCodeProperty = AccessTools.Property(typeof(ZPlayFabMatchmaking), "JoinCode");
        var drawn = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < 3; i++)
        {
            generate.Invoke(_lobbyFixture, null);
            string code = (string)joinCodeProperty.GetValue(null);
            Check(code.Length == 6 && code.All(char.IsDigit), "join code draw " + i + " is six digits");
            Check((string)ReadMember(serverData, "joinCode")! == code, "join code draw " + i + " lands in the server data");
            drawn.Add(code);
        }
        Check(drawn.Count > 1, "join code draws are not a fixed sequence");
        Check(CrossplayLobbyCompatibility.RandomJoinCode().Length == 6, "random join code helper pads to six digits");

        object result = Activator.CreateInstance(callback.GetParameters()[0].ParameterType);
        PropertyInfo? listProperty = AccessTools.Property(result.GetType(), "Lobbies");
        Type listType = listProperty?.PropertyType ?? AccessTools.Field(result.GetType(), "Lobbies").FieldType;
        var lobbies = (IList)Activator.CreateInstance(listType);
        Type lobbyType = listType.GetGenericArguments()[0];
        object ownerless = Activator.CreateInstance(lobbyType);
        lobbies.Add(ownerless);
        WriteMember(result, "Lobbies", lobbies);

        // Reproduce the exact vanilla callback failure before the guard is installed.
        pluginHarmony.Unpatch(callback, prefix);
        bool reproduced = false;
        try { callback.Invoke(_lobbyFixture, new[] { result }); }
        catch (TargetInvocationException exception) { reproduced = exception.InnerException is NullReferenceException; }
        finally { CrossplayLobbyCompatibility.Apply(pluginHarmony); }
        Check(reproduced, "vanilla join-code callback throws for one ownerless lobby");

        _harmony!.Patch(AccessTools.Method(typeof(ZPlayFabMatchmaking), "RegenerateLobbyJoinCode"),
            prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(CaptureRegeneration)));
        _harmony.Patch(AccessTools.Method(typeof(ZPlayFabMatchmaking), "ActivateSession"),
            prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(CaptureActivation)));
        _regenerated = _activated = 0;
        callback.Invoke(_lobbyFixture, new[] { result });
        Check(_regenerated == 1 && _activated == 0, "ownerless lobby takes native join-code regeneration");
        Check(ReadMember(_lobbyFixture, "m_state")!.ToString() == "RegenerateJoinCode", "native collision state transition retained");
        Check(ReferenceEquals(ReadMember(result, "Lobbies"), lobbies) && lobbies.Count == 1 && ReadMember(ownerless, "Owner") == null,
            "ownerless provider response remains unchanged");
        lobbies.Add(Activator.CreateInstance(lobbyType));
        callback.Invoke(_lobbyFixture, new[] { result });
        Check(_regenerated == 2, "multiple lobby collisions retain native regeneration");
        lobbies.Clear();
        WriteMember(_lobbyFixture, "m_retries", 10);
        callback.Invoke(_lobbyFixture, new[] { result });
        Check((int)ReadMember(_lobbyFixture, "m_retries")! == 9 && (float)ReadMember(_lobbyFixture, "m_retryIn")! == 1f,
            "empty lobby index retains native bounded retry");

        object manager = AccessTools.Property(typeof(PlayFabManager), "instance").GetValue(null);
        PropertyInfo entityProperty = AccessTools.Property(typeof(PlayFabManager), "Entity");
        object? savedEntity = entityProperty.GetValue(manager);
        try
        {
            object localEntity = Activator.CreateInstance(entityProperty.PropertyType);
            WriteMember(localEntity, "Id", "regression-local");
            WriteMember(localEntity, "Type", "title_player_account");
            entityProperty.SetValue(manager, localEntity);
            Type ownerType = AccessTools.Property(lobbyType, "Owner")?.PropertyType ?? AccessTools.Field(lobbyType, "Owner").FieldType;
            object owner = Activator.CreateInstance(ownerType);
            WriteMember(owner, "Id", "regression-local");
            WriteMember(ownerless, "Owner", owner);
            lobbies.Add(ownerless);
            callback.Invoke(_lobbyFixture, new[] { result });
            Check(_activated == 1 && _regenerated == 2, "unique own lobby still activates normally");
            WriteMember(owner, "Id", "regression-other");
            callback.Invoke(_lobbyFixture, new[] { result });
            Check(_activated == 1 && _regenerated == 3, "foreign owned lobby retains native collision handling");
        }
        finally
        {
            entityProperty.SetValue(manager, savedEntity);
            _lobbyFixture = null;
        }
    }

    private void TestBitmapStorage()
    {
        foreach (object storage in new object[] { new bool[64], new BitArray(64) })
        {
            Check(ExplorationBitmap.TryWrap(storage, out ExplorationBitmap map), "accept " + storage.GetType().Name);
            map[0] = true; map[7] = true; map[63] = true;
            var copy = new bool[64];
            map.CopyTo(copy);
            Check(copy[0] && copy[7] && copy[63] && !copy[8], "bitmap endpoints and copy");
            Check(storage is bool[] array ? array[63] : ((BitArray)storage)[63], "write through to native storage");
            var sent = new bool[64];
            var encoded = (List<ExploredMapRange[]>)Call(typeof(MapSharingModule), "EncodeRanges", map, sent, 8)!;
            Check(encoded.Count == 1 && encoded[0].Length == 3 && sent[63], "encode row boundaries and sent bits");
            Check(((List<ExploredMapRange[]>)Call(typeof(MapSharingModule), "EncodeRanges", map, sent, 8)!).Count == 0,
                "incremental exchange does not echo sent pixels");
        }
        Check(!ExplorationBitmap.TryWrap(null, out _) && !ExplorationBitmap.TryWrap(new byte[64], out _),
            "unsupported storage fails closed");
    }

    private MapSharingModule MapModule(string name, string store)
    {
        string config = Path.Combine(_root, name + ".cfg");
        File.WriteAllText(config, "[MapSharing]\nEnabled = true\nSharedExploration = true\nExplorationSyncSeconds = 10\n");
        var settings = new ValheimOneConfig(config);
        var module = new MapSharingModule(settings.Features, new ModLogger(Logger));
        Set(module, "_storageDirectory", store);
        return module;
    }

    private Minimap Minimap(int first, int second)
    {
        var obj = new GameObject("regression-minimap");
        obj.SetActive(false);
        _objects.Add(obj);
        Minimap map = obj.AddComponent<Minimap>();
        map.m_textureSize = 8;
        FieldInfo field = AccessTools.Field(typeof(Minimap), "m_explored");
        object explored = field.FieldType == typeof(BitArray) ? new BitArray(64) : new bool[64];
        field.SetValue(map, explored);
        ExplorationBitmap.TryWrap(explored, out ExplorationBitmap bitmap);
        bitmap[first] = bitmap[second] = true;
        var fog = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        var colors = new Color32[64];
        for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 77, 99, 255);
        fog.SetPixels32(colors); fog.Apply();
        Set(map, "m_fogTexture", fog);
        return map;
    }

    private static ZPackage MapPacket(int transfer, params int[] pixels)
    {
        var package = new ZPackage();
        package.Write(transfer); package.Write(1); package.Write(0); package.Write(8); package.Write(pixels.Length);
        foreach (int pixel in pixels) { package.Write(pixel % 8); package.Write(pixel % 8); package.Write(pixel / 8); }
        return new ZPackage(package.GetArray());
    }

    private void TestWebFogCartography()
    {
        Assembly assembly = typeof(ValheimOnePlugin).Assembly;
        Type fogType = assembly.GetType("ValheimOne.LiveMap.FogTracker", true)!;
        Type readerType = assembly.GetType("ValheimOne.LiveMap.MapTableReader", true)!;
        string directory = Path.Combine(_root, "web-fog");
        var log = new ModLogger(Logger);
        object fog = Activator.CreateInstance(fogType, directory, log)!;
        object reader = Activator.CreateInstance(readerType, fog, log)!;
        object Snapshot(object tracker) => AccessTools.Property(fogType, "Snapshot").GetValue(tracker, null)!;
        bool Explored(object tracker, float x, float z) => (bool)Call(fogType, "IsExplored", Snapshot(tracker), x, z)!;
        try
        {
            Check((float)AccessTools.Field(fogType, "WorldSpan").GetRawConstantValue() == 24576f,
                "web fog retains its canonical world-space extent");
            Check((bool)Call(fog, "Stamp", 3000f, -3000f)!, "off-center player trail is recorded");
            Call(fog, "MarkChanged");
            Call(fog, "PublishPending", true);
            Check(Explored(fog, 3000f, -3000f) && !Explored(fog, 6000f, -6000f),
                "trail data is at the real position, not the doubled display position");

            // Shared-map version 3 still writes one boolean byte per native 2048-square
            // minimap cell in Valheim 1.0, including when storage is a BitArray.
            var cells = new byte[2048 * 2048];
            cells[970 * 2048 + 1142] = 1;
            cells[1410 * 2048 + 514] = 1;
            var package = new ZPackage();
            package.Write(3);
            package.Write(cells);
            package.Write(1);
            package.Write(42L);
            package.Write("Survey");
            package.Write(new Vector3(1416f, 0f, -648f));
            package.Write(0);
            package.Write(false);
            package.Write("Fixture");
            object parsed = Call(reader, "ParseTable", global::Utils.Compress(package.GetArray()))!;
            var mask = (byte[])AccessTools.Property(parsed.GetType(), "ExploredMask").GetValue(parsed, null)!;
            Check(mask.Length == 512 * 512 && mask[269 * 512 + 285] != 0 && mask[159 * 512 + 128] != 0,
                "native compressed cartography data preserves off-center explored cells");
            var pins = (Array)AccessTools.Property(parsed.GetType(), "Pins").GetValue(parsed, null)!;
            Check(pins.Length == 1, "cartography pin framing survives the exploration bitmap");
            Call(fog, "OrExternalMask", mask);
            Call(fog, "PublishPending", true);
            Check(Explored(fog, 1416f, -648f) && Explored(fog, -6120f, 4632f) && Explored(fog, 3000f, -3000f),
                "cartography data and new trails merge without moving or losing either");
            Check(!Explored(fog, 2832f, -1296f), "unvisited doubled cartography position stays covered");
            Call(fog, "Stop");
            object restored = Activator.CreateInstance(fogType, directory, log)!;
            try { Check(Explored(restored, 1416f, -648f) && Explored(restored, 3000f, -3000f),
                "existing fog cache preserves cartography and trails after reload"); }
            finally { Call(restored, "Stop"); }
        }
        finally { Call(fog, "Stop"); }
    }

    private void TestMapExchange()
    {
        string store = Path.Combine(_root, "map-union-" + Guid.NewGuid().ToString("N"));
        MapSharingModule server = MapModule("server", store);
        MapSharingModule a = MapModule("client-a", Path.Combine(_root, "map-a"));
        MapSharingModule b = MapModule("client-b", Path.Combine(_root, "map-b"));
        Minimap ma = Minimap(0, 9), mb = Minimap(37, 63);
        Call(a, "BeginClientMap", ma); Call(b, "BeginClientMap", mb);
        Check(Get(a, "_clientSent") is bool[] && Get(b, "_clientSent") is bool[], "enabled native minimap initializes both peers");
        Call(server, "HandleMap", RemotePeer, MapPacket(1, 0, 9));
        Check(Get(server, "_serverUnion") == null, "unhandshaken map sender rejected");
        ((IVersionHandshakeExtension)server).OnPeerCompatible(RemotePeer);
        ((IVersionHandshakeExtension)server).OnPeerCompatible(RemotePeer + 1);
        Call(server, "HandleMap", RemotePeer, MapPacket(1, 0, 9));
        Call(server, "HandleMap", RemotePeer + 1, MapPacket(2, 37, 63));
        bool[] union = (bool[])Get(server, "_serverUnion")!;
        Check(union[0] && union[9] && union[37] && union[63] && !union[10], "server merges two explored regions only");
        Call(a, "HandleIncomingChunk", RemotePeer, MapPacket(3, 0, 9, 37, 63), false);
        Call(b, "HandleIncomingChunk", RemotePeer, MapPacket(4, 0, 9, 37, 63), false);
        foreach (Minimap map in new[] { ma, mb })
        {
            Check(GameCompat.TryGetExploredMap(map, out ExplorationBitmap bitmap) && bitmap[0] && bitmap[9] && bitmap[37] && bitmap[63],
                "received exploration reaches actual minimap storage");
            Color32[] colors = ((Texture2D)Get(map, "m_fogTexture")!).GetPixels32();
            Check(colors[37].r == 0 && colors[37].g == 77 && colors[10].r == 255,
                "fog clears explored pixels and preserves unknown fog and other channel");
        }
        Set(server, "_nextPersistenceAt", 0f);
        Call(server, "PumpServer", ZNet.instance, ZRoutedRpc.instance, new[] { RemotePeer, RemotePeer + 1 });
        Check(Directory.GetFiles(store, "*_mapSync.bin").Length == 1, "shared exploration persisted to disk");
        MapSharingModule reload = MapModule("server-reload", store);
        Call(reload, "EnsureServerWorld", ZNet.instance, 8);
        bool[] restored = (bool[])Get(reload, "_serverUnion")!;
        Check(restored[0] && restored[9] && restored[37] && restored[63] && !restored[10], "server restart restores exploration union");
        Call(server, "HandleMap", RemotePeer, MapPacket(5, 10));
        Check(((bool[])Get(server, "_serverUnion")!)[10], "later exploration delta is merged");
        Set(ma, "m_explored", AccessTools.Field(typeof(Minimap), "m_explored").FieldType == typeof(BitArray)
            ? (object)new BitArray(1) : new bool[1]);
        Check(!(bool)Call(a, "ApplyClientRanges", 8, new[] { new ExploredMapRange(0, 7, 7) })!,
            "changed minimap dimensions reject writes");
    }

    private Player NewPlayer(Vector3 position)
    {
        var obj = new GameObject("regression-player");
        obj.SetActive(false);
        obj.transform.position = position;
        _objects.Add(obj);
        ZNetView view = obj.AddComponent<ZNetView>();
        ZDO zdo = ZDOMan.instance.CreateNewZDO(position, 0);
        Set(view, "m_zdo", zdo);
        Player player = obj.AddComponent<Player>();
        Set(player, "m_nview", view);
        Set(player, "m_inventory", new Inventory("regression-player", null, 8, 4));
        zdo.Set(ZDOVars.s_playerID, 123456L);
        Set(typeof(Player), "m_localPlayer", player);
        return player;
    }

    private Container NewChest(Vector3 position, int wood)
    {
        GameObject obj = Instantiate(ZNetScene.instance.GetPrefab("piece_chest_wood"), position, Quaternion.identity);
        _objects.Add(obj);
        Container container = obj.GetComponent<Container>();
        container.m_checkGuardStone = false;
        container.GetInventory().RemoveAll();
        container.GetInventory().AddItem(ObjectDB.instance.GetItemPrefab("Wood"), wood);
        CapturedViews.Add(ChestScanner.GetNetworkView(container)!);
        return container;
    }

    private void TestChestTransfer()
    {
        Vector3 position = new Vector3(0, 600, 0);
        Player player = NewPlayer(position);
        Container authority = NewChest(position + Vector3.right, 10);
        Container replica = NewChest(position + Vector3.left, 3);
        Container local = NewChest(position + Vector3.forward, 2);
        ZNetView authorityView = ChestScanner.GetNetworkView(authority)!;
        ZNetView replicaView = ChestScanner.GetNetworkView(replica)!;
        ZDO ownerZdo = authorityView.GetZDO(), clientZdo = replicaView.GetZDO();
        long owner = ZDOMan.GetSessionID();
        clientZdo.SetOwner(owner);
        SetProperty(clientZdo, "Owner", false); // This object represents the other process's replica.
        var preview = new ChestScanner(includeRemote: true);
        var automation = new ChestScanner();
        Check(Contains(preview.GetInventories(player, 20, false, 1), replica.GetInventory()), "remote-owned chest appears in preview");
        Check(!Contains(automation.GetInventories(player, 20, false, 1), replica.GetInventory()), "default scanner retains owned-only inventory access");
        Set(replica, "m_inUse", true);
        Check(!Contains(preview.GetInventories(player, 20, false, 1), replica.GetInventory()), "open chest excluded");
        Set(replica, "m_inUse", false);
        clientZdo.Set(ZDOVars.s_inUse, 1);
        Check(!Contains(preview.GetInventories(player, 20, false, 1), replica.GetInventory()), "remote in-use flag excluded");
        clientZdo.Set(ZDOVars.s_inUse, 0);
        replica.m_privacy = Container.PrivacySetting.Group;
        Check(!Contains(preview.GetInventories(player, 20, true, 1), replica.GetInventory()), "privacy applies even with ward bypass");
        replica.m_privacy = Container.PrivacySetting.Public;
        replica.m_checkGuardStone = true;
        _denyWard = true;
        Check(!Contains(preview.GetInventories(player, 20, false, 1), replica.GetInventory()), "ward denial excludes chest");
        Check(Contains(preview.GetInventories(player, 20, true, 1), replica.GetInventory()), "explicit ward bypass permits otherwise public chest");
        _denyWard = false;
        replica.m_checkGuardStone = false;

        // Make the authority revision newer than the replica, with unchanged inventory.
        for (int i = 0; i < 20; i++) ownerZdo.Set("regression_revision", i);
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending, "foreign ownership does not authorize immediate consumption");
        var request = Packets.Dequeue();
        Check(request.Target == owner && request.Name == "VO_CraftChestRequest", "request sent to current owner");
        Call(typeof(ChestOwnership), "HandleRequest", authority, RemotePeer, new ZPackage(request.Data));
        var reply = Packets.Dequeue();
        Check(reply.Target == RemotePeer && !authorityView.IsOwner(), "only owner grants handoff and relinquishes ownership");
        Call(typeof(ChestOwnership), "HandleReply", replica, owner + 1, new ZPackage(reply.Data));
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending, "spoofed grant ignored");
        Call(typeof(ChestOwnership), "HandleReply", replica, owner, new ZPackage(reply.Data));
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending, "grant before ownership update stays pending");
        SetProperty(clientZdo, "Owner", true);
        clientZdo.OwnerRevision = ownerZdo.OwnerRevision;
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending, "ownership before inventory update stays pending");
        CopyItems(ownerZdo, clientZdo);
        clientZdo.DataRevision = ownerZdo.DataRevision;
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Ready, "grant plus exact ownership and current data authorize use");
        Check(replica.GetInventory().CountItems("$item_wood") == 10, "handoff loads authoritative inventory rather than stale preview");

        // Exclude the authority replica from the player's physical scan now that transfer completed.
        authority.transform.position = position + Vector3.right * 100;
        player.GetInventory().AddItem(ObjectDB.instance.GetItemPrefab("Wood"), 2);
        CraftFromChestModule module = (CraftFromChestModule)Get(typeof(CraftFromChestModule), "_active")!;
        var requirement = new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>(), m_amount = 5 };
        var requirements = new[] { requirement };
        Check((ChestOwnership.Result)Call(module, "PrepareResources", player, requirements, 0, 1)! == ChestOwnership.Result.Ready,
            "craft preflight succeeds after remote chest handoff");
        int before = player.GetInventory().CountItems("$item_wood") + replica.GetInventory().CountItems("$item_wood") + local.GetInventory().CountItems("$item_wood");
        player.ConsumeResources(requirements, 0);
        int after = player.GetInventory().CountItems("$item_wood") + replica.GetInventory().CountItems("$item_wood") + local.GetInventory().CountItems("$item_wood");
        Check(before - after == 5 && player.GetInventory().CountItems("$item_wood") == 0,
            "native consumption removes exact cost and uses backpack first");
        CopyItems(clientZdo, ownerZdo);
        ownerZdo.DataRevision++;
        ChestScanner.RefreshInventory(authority);
        Check(authority.GetInventory().CountItems("$item_wood") == replica.GetInventory().CountItems("$item_wood"),
            "saved consumption replicates back to previous owner");

        FieldInfo? upgraderField = AccessTools.Field(typeof(Piece.Requirement), "m_upgraderResource");
        if (upgraderField != null)
        {
            var upgradeOnly = new Piece.Requirement { m_resItem = requirement.m_resItem, m_amount = 100 };
            upgraderField.SetValue(upgradeOnly, true);
            var normal = new Piece.Requirement { m_resItem = requirement.m_resItem, m_amount = 3 };
            Check((ChestOwnership.Result)Call(module, "PrepareResources", player, new[] { normal, upgradeOnly }, 1, 1)! == ChestOwnership.Result.Ready,
                "normal crafting excludes upgrader-only ingredients");
            int total = replica.GetInventory().CountItems("$item_wood") + local.GetInventory().CountItems("$item_wood");
            player.ConsumeResources(new[] { normal, upgradeOnly }, 1);
            Check(total - replica.GetInventory().CountItems("$item_wood") - local.GetInventory().CountItems("$item_wood") == 3,
                "normal recipe removes only its actual cost on 1.0");
        }
        SetProperty(clientZdo, "Owner", false);
        clientZdo.SetOwner(RemotePeer + 5);
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending, "ownership loss requires a new grant");
        int count = replica.GetInventory().CountItems("$item_wood");
        Call(typeof(ChestOwnership), "HandleReply", replica, owner, new ZPackage(reply.Data));
        Check(ChestOwnership.Prepare(replica, player) == ChestOwnership.Result.Pending && replica.GetInventory().CountItems("$item_wood") == count,
            "old grant cannot authorize new transfer or remove items");
        local.GetInventory().RemoveAll();
        requirement.m_amount = 1;
        var recipe = ScriptableObject.CreateInstance<Recipe>();
        recipe.m_resources = requirements;
        recipe.m_item = ObjectDB.instance.GetItemPrefab("Hammer").GetComponent<ItemDrop>();
        var guiObject = new GameObject("regression-crafting-gui");
        guiObject.SetActive(false);
        _objects.Add(guiObject);
        InventoryGui gui = guiObject.AddComponent<InventoryGui>();
        Set(gui, "m_craftRecipe", recipe);
        Call(gui, "DoCrafting", player);
        Check(player.GetInventory().CountItems(recipe.m_item.m_itemData.m_shared.m_name) == 0,
            "pending transfer cannot create crafted output");
        object[] uiArgs = { player, 0.25f, recipe, null!, false, 1, 0.5f };
        bool updateUi = (bool)Call(typeof(CraftFromChestModule), "UpdateRecipePrefix", uiArgs)!;
        Check(updateUi && (float)uiArgs[1] == 0f && (float)uiArgs[6] == 0.5f,
            "pending transfer freezes craft clock while preserving normal progress and cancel controls");
        uiArgs[1] = 0.25f; uiArgs[6] = -1f;
        Call(typeof(CraftFromChestModule), "UpdateRecipePrefix", uiArgs);
        Check((float)uiArgs[6] == -1f && (float)Get(module, "_craftWaitStarted")! < 0f,
            "cancelled craft remains cancelled");

        var ghostObject = new GameObject("regression-build-preview");
        ghostObject.SetActive(false);
        ghostObject.transform.position = position;
        _objects.Add(ghostObject);
        Piece piece = ghostObject.AddComponent<Piece>();
        piece.m_resources = requirements;
        Set(player, "m_placementGhost", ghostObject);
        Set(player, "m_placePressedTime", -9999f);
        Check(!player.TryPlacePiece(piece), "pending transfer prevents native building placement");
        Check((float)Get(player, "m_placePressedTime")! == -9999f, "pending placement never schedules a delayed build input");
        ghostObject.transform.position += Vector3.right;
        Check(!player.TryPlacePiece(piece) && (float)Get(player, "m_placePressedTime")! == -9999f,
            "moving the build preview cannot trigger delayed placement");

        ownerZdo.SetOwner(owner);
        Set(authority, "m_inUse", true);
        Packets.Clear();
        var busyRequest = new ZPackage(); busyRequest.Write(778); busyRequest.Write(player.GetPlayerID());
        Call(typeof(ChestOwnership), "HandleRequest", authority, RemotePeer, new ZPackage(busyRequest.GetArray()));
        var busyReply = new ZPackage(Packets.Dequeue().Data);
        Check(busyReply.ReadInt() == 778 && !busyReply.ReadBool() && authorityView.IsOwner(),
            "owner refuses open chest without handing it away");
        Set(authority, "m_inUse", false);
        Packets.Clear();
    }

    private GameObject NewStation(string name, Vector3 position)
    {
        GameObject prefab = ZNetScene.instance.GetPrefab(name);
        Check(prefab != null, "native station prefab exists: " + name);
        GameObject obj = Instantiate(prefab, position, Quaternion.identity)!;
        _objects.Add(obj);
        return obj;
    }

    private void TestStations()
    {
        Vector3 position = new Vector3(500, 600, 500);
        Player player = NewPlayer(position);
        Container chest = NewChest(position, 8);
        chest.GetInventory().AddItem(ObjectDB.instance.GetItemPrefab("Coal"), 8);
        chest.GetInventory().AddItem(ObjectDB.instance.GetItemPrefab("CopperOre"), 8);
        chest.GetInventory().AddItem(ObjectDB.instance.GetItemPrefab("RawMeat"), 8);
        var scanner = new ChestScanner(includeRemote: true);
        IReadOnlyList<Inventory> inventories = scanner.GetInventories(player, 20, false, 1);
        Smelter smelter = NewStation("smelter", position + Vector3.right * 2).GetComponent<Smelter>();
        ZNetView view = smelter.GetComponent<ZNetView>();
        Check(smelter.m_secPerProduct == 5f && smelter.m_maxOre == 12 && smelter.m_maxFuel == 12,
            "production overrides applied on native smelter Awake");

        int coal = chest.GetInventory().CountItems("$item_coal");
        Call(typeof(StationAutomationModule), "TryAddSmelterFuel", smelter, view, inventories, scanner, player);
        Check((float)Call(smelter, "GetFuel")! == 1f && chest.GetInventory().CountItems("$item_coal") == coal - 1,
            "native smelter fuel RPC consumes and adds exactly one fuel");
        ItemDrop.ItemData ore = chest.GetInventory().GetItem("$item_copperore");
        FieldInfo cheated = AccessTools.Field(typeof(ItemDrop.ItemData), "m_cheated");
        cheated?.SetValue(ore, true);
        int ores = chest.GetInventory().CountItems("$item_copperore");
        Call(typeof(StationAutomationModule), "TryAddSmelterOre", smelter, view, inventories, scanner, player);
        Check((int)Call(smelter, "GetQueueSize")! == 1 && chest.GetInventory().CountItems("$item_copperore") == ores - 1,
            "native ore RPC receives full payload and queues consumed input");
        Check(view.GetZDO().GetBool(ZDOVars.s_cheatedQueued), "smelter RPC preserves the source item's cheated flag");
        ItemDrop.ItemData invalid = ore.Clone(); invalid.m_dropPrefab = null;
        Check(!GameCompat.TryGetSmelterItemArguments(invalid, out _), "invalid station input cannot build a consumable RPC payload");

        // Advance only this station's native accumulator, then observe a real product.
        var existingDrops = new HashSet<ItemDrop>(UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None));
        view.GetZDO().Set(ZDOVars.s_fuel, 10f);
        Call(smelter, "SetAccumulator", 5f);
        Call(smelter, "UpdateSmelter");
        int copper = 0;
        foreach (ItemDrop drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (existingDrops.Contains(drop)) continue;
            _objects.Add(drop.gameObject);
            if (drop.m_itemData.m_shared.m_name == "$item_copper") copper += drop.m_itemData.m_stack;
        }
        Check(copper == 1, "native production loop turns the queued ore into one copper output");

        Smelter kiln = NewStation("charcoal_kiln", position + Vector3.back * 3).GetComponent<Smelter>();
        ZNetView kilnView = kiln.GetComponent<ZNetView>();
        int wood = chest.GetInventory().CountItems("$item_wood");
        Call(typeof(StationAutomationModule), "TryAddSmelterOre", kiln, kilnView, inventories, scanner, player);
        Check((int)Call(kiln, "GetQueueSize")! == 1 && chest.GetInventory().CountItems("$item_wood") == wood - 1,
            "native kiln queue receives the wood taken from its chest");

        CookingStation cooking = NewStation("piece_cookingstation", position + Vector3.left * 3).GetComponent<CookingStation>();
        ZNetView cookingView = cooking.GetComponent<ZNetView>();
        string rawName = ObjectDB.instance.GetItemPrefab("RawMeat").GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
        ItemDrop.ItemData raw = chest.GetInventory().GetItem(rawName);
        Check(raw != null, "native raw-meat input is present");
        cheated?.SetValue(raw, true);
        int rawCount = raw!.m_stack;
        Call(typeof(CookingStationModule), "TryConsumeAndInvokeRaw", cooking, cookingView, inventories, scanner, player);
        Check(cookingView.GetZDO().GetString("slot0") == "RawMeat" &&
            chest.GetInventory().CountItems(rawName) == rawCount - 1,
            "native cooking RPC puts the consumed raw food into a cooking slot");
        Check(cookingView.GetZDO().GetBool(ZDOVars.s_cheatedQueued), "cooking RPC preserves the source item's cheated flag");

        Set(chest, "m_inUse", true);
        ores = chest.GetInventory().CountItems("$item_copperore");
        int queued = (int)Call(smelter, "GetQueueSize")!;
        Call(typeof(StationAutomationModule), "TryAddSmelterOre", smelter, view, inventories, scanner, player);
        Check(chest.GetInventory().CountItems("$item_copperore") == ores && (int)Call(smelter, "GetQueueSize")! == queued,
            "automation rechecks busy chests before removing an item");
        Set(chest, "m_inUse", false);

        ZDO chestZdo = ChestScanner.GetNetworkView(chest)!.GetZDO();
        chestZdo.SetOwner(RemotePeer + 10);
        ores = chest.GetInventory().CountItems("$item_copperore");
        Call(typeof(StationAutomationModule), "TryAddSmelterOre", smelter, view, inventories, scanner, player);
        Check(chest.GetInventory().CountItems("$item_copperore") == ores && (int)Call(smelter, "GetQueueSize")! == queued,
            "automation waits for foreign chest ownership without consuming input");
        Check(Packets.Count > 0, "automation requests a safe chest handoff");
        Packets.Clear();
    }

    private void TestProductionSettings()
    {
        var settings = new ValheimOneConfig(Path.Combine(_root, "production-overlay.cfg"));
        var module = new ProductionSpeedsModule(settings.Features, new ModLogger(Logger));
        object? previous = Get(typeof(ProductionSpeedsModule), "_active");
        var stations = new List<(Smelter Station, float Seconds, int Queue)>();
        string[] prefabs = { "smelter", "blastfurnace", "charcoal_kiln", "windmill", "piece_spinningwheel", "eitrrefinery" };
        string[] prefixes = { "Smelter", "Furnace", "Kiln", "Windmill", "SpinningWheel", "EitrRefinery" };
        try
        {
            Set(typeof(ProductionSpeedsModule), "_active", module);
            for (int i = 0; i < prefabs.Length; i++)
            {
                Smelter station = NewStation(prefabs[i], new Vector3(700 + i * 5, 600, 700)).GetComponent<Smelter>();
                stations.Add((station, station.m_secPerProduct, station.m_maxOre));
            }
            string overlay = "[ProductionSpeeds] / Enabled=true\n";
            foreach (string prefix in prefixes)
                overlay += "[ProductionSpeeds] / " + prefix + "ProductionSeconds=7\n" +
                    "[ProductionSpeeds] / " + prefix + "MaxQueue=13\n";
            Check(settings.ApplyOverlay(overlay) == 13, "all six station production settings arrive through the synced overlay");
            foreach (var row in stations)
                Check(row.Station.m_secPerProduct == 7 && row.Station.m_maxOre == 13,
                    "live production settings apply to " + row.Station.name);
            settings.ClearOverlay();
            foreach (var row in stations)
                Check(row.Station.m_secPerProduct == row.Seconds && row.Station.m_maxOre == row.Queue,
                    "disabling production overrides restores " + row.Station.name);
        }
        finally { Set(typeof(ProductionSpeedsModule), "_active", previous); }
    }

    private void TestWeatherDamage()
    {
        var settings = new ValheimOneConfig(Path.Combine(_root, "structural-overlay.cfg"));
        var module = new StructuralIntegrityModule(settings.Features);
        object? previous = Get(typeof(StructuralIntegrityModule), "_active");
        try
        {
            Set(typeof(StructuralIntegrityModule), "_active", module);
            WearNTear wall = NewStation("woodwall", new Vector3(760, 600, 700)).GetComponent<WearNTear>()!;
            Check(wall.m_noRoofWear, "a native wood wall wears without a roof by default");
            Check(settings.ApplyOverlay("[StructuralIntegrity] / Enabled=true\n[StructuralIntegrity] / NoWeatherDamage=true\n") == 2,
                "no weather damage arrives through the synced overlay");
            Check(!wall.m_noRoofWear, "no weather damage switches the wall's rain and water wear off");
            Check(settings.ApplyOverlay("[StructuralIntegrity] / Enabled=true\n[StructuralIntegrity] / NoWeatherDamage=false\n") == 2,
                "no weather damage can be switched back off live");
            Check(wall.m_noRoofWear, "switching no weather damage off restores the wall's own wear flag");
            settings.ClearOverlay();
            Check(wall.m_noRoofWear, "disabling structural integrity leaves the wall on its own wear flag");
        }
        finally { Set(typeof(StructuralIntegrityModule), "_active", previous); }
    }

    private void TestFoodDegradation()
    {
        var settings = new ValheimOneConfig(Path.Combine(_root, "food-overlay.cfg"));
        var module = new FoodDurationModule(settings.Features);
        object? previous = Get(typeof(FoodDurationModule), "_active");
        try
        {
            Set(typeof(FoodDurationModule), "_active", module);
            Player player = NewPlayer(new Vector3(800, 600, 800));
            player.SetSeenTutorial("eitr");
            var food = new Player.Food
            {
                m_item = ObjectDB.instance.GetItemPrefab("CookedMeat").GetComponent<ItemDrop>().m_itemData.Clone(),
                m_time = 50f,
            };
            // Keep the native edible item private to the fixture and exercise all
            // three benefits through the game's real UpdateFood/SetMax methods.
            food.m_item.m_shared = new ItemDrop.ItemData.SharedData
            {
                m_name = "regression-food", m_foodBurnTime = 100f,
                m_food = 80f, m_foodStamina = 60f, m_foodEitr = 40f,
            };
            player.GetFoods().Add(food);
            float baseHealth = (float)Get(player, "m_baseHP")!;
            float baseStamina = (float)Get(player, "m_baseStamina")!;
            Check(settings.ApplyOverlay("[Food] / Enabled=true\n[Food] / NoDegradation=true\n") == 2,
                "food no-degradation arrives through the synced overlay");
            Call(player, "UpdateFood", 0f, true);
            Check(Math.Abs(player.GetMaxHealth() - (baseHealth + 80f)) < 0.01f,
                "no-degradation reaches the native maximum health, not just food records");
            Check(Math.Abs((float)Get(player, "m_maxStamina")! - (baseStamina + 60f)) < 0.01f,
                "no-degradation reaches the native maximum stamina");
            Check(Math.Abs((float)Get(player, "m_maxEitr")! - 40f) < 0.01f,
                "no-degradation reaches the native maximum eitr");
            Check(food.m_time == 49f, "no-degradation leaves the food expiry timer running");
            Check(food.m_health == 80f && food.m_stamina == 60f && food.m_eitr == 40f,
                "food display values agree with native maximum benefits");
            settings.ApplyOverlay("[Food] / Enabled=true\n[Food] / NoDegradation=false\n");
            Call(player, "UpdateFood", 0f, true);
            float decay = Mathf.Pow(48f / 100f, 0.3f);
            Check(Math.Abs(player.GetMaxHealth() - (baseHealth + 80f * decay)) < 0.01f &&
                Math.Abs((float)Get(player, "m_maxStamina")! - (baseStamina + 60f * decay)) < 0.01f &&
                Math.Abs((float)Get(player, "m_maxEitr")! - 40f * decay) < 0.01f,
                "switching no-degradation off restores native decay for all benefits");
            settings.ApplyOverlay("[Food] / Enabled=false\n[Food] / NoDegradation=true\n");
            Call(player, "UpdateFood", 0f, true);
            Check(food.m_health < 80f && player.GetMaxHealth() < baseHealth + 80f,
                "disabled Food module preserves native decay even with NoDegradation configured");
            settings.ApplyOverlay("[Food] / Enabled=true\n[Food] / NoDegradation=true\n");
            food.m_time = 2f;
            Call(player, "UpdateFood", 0f, true);
            Check(food.m_time == 1f && player.GetMaxHealth() == baseHealth + 80f,
                "full benefits remain until the last food second");
            Call(player, "UpdateFood", 0f, true);
            Check(player.GetFoods().Count == 0 && player.GetMaxHealth() == baseHealth &&
                (float)Get(player, "m_maxStamina")! == baseStamina && (float)Get(player, "m_maxEitr")! == 0f,
                "expired food is removed and its benefits end normally");
        }
        finally { Set(typeof(FoodDurationModule), "_active", previous); }
    }

    private static void CopyItems(ZDO source, ZDO destination)
    {
        // Container storage also changed in 1.0. Exercise the installed game's exact format.
        byte[]? bytes = source.GetByteArray(ZDOVars.s_items);
        if (bytes != null) destination.Set(ZDOVars.s_items, (byte[])bytes.Clone());
        else destination.Set(ZDOVars.s_items, source.GetString(ZDOVars.s_items));
    }

    private static bool Contains(IReadOnlyList<Inventory> list, Inventory inventory)
    {
        foreach (Inventory candidate in list) if (ReferenceEquals(candidate, inventory)) return true;
        return false;
    }

    private static object? Get(object target, string name) =>
        AccessTools.Field(target as Type ?? target.GetType(), name).GetValue(target is Type ? null : target);
    private static void Set(object target, string name, object? value) =>
        AccessTools.Field(target as Type ?? target.GetType(), name).SetValue(target is Type ? null : target, value);
    private static void SetProperty(object target, string name, object value) =>
        AccessTools.Property(target.GetType(), name).SetValue(target, value, null);
    // The live map reads Port, BindIp and TextureSize only when it starts. Its Update must see a
    // change to any of them against the values of the last start so it can restart (0.13.22).
    private void TestMapStartSettings()
    {
        Type behaviourType = typeof(ValheimOnePlugin).Assembly.GetType("ValheimOne.LiveMap.LiveMapBehaviour", true)!;
        object? behaviour = AccessTools.Property(behaviourType, "Instance").GetValue(null, null);
        Check(behaviour != null, "live map behaviour exists");
        object config = AccessTools.Field(behaviourType, "_config").GetValue(behaviour)!;
        MethodInfo changed = AccessTools.Method(behaviourType, "StartSettingsChanged");
        FieldInfo port = AccessTools.Field(behaviourType, "_attemptedPort");
        FieldInfo bindIp = AccessTools.Field(behaviourType, "_attemptedBindIp");
        FieldInfo textureSize = AccessTools.Field(behaviourType, "_attemptedTextureSize");
        object? oldPort = port.GetValue(behaviour), oldBindIp = bindIp.GetValue(behaviour), oldTexture = textureSize.GetValue(behaviour);
        int currentPort = (int)ReadMember(config, "Port")!;
        string currentBindIp = (string?)ReadMember(config, "BindIp") ?? "";
        int currentTexture = (int)ReadMember(config, "TextureSize")!;
        bool Changed() => (bool)changed.Invoke(behaviour, new[] { config })!;
        try
        {
            port.SetValue(behaviour, currentPort);
            bindIp.SetValue(behaviour, currentBindIp);
            textureSize.SetValue(behaviour, currentTexture);
            Check(!Changed(), "map start settings unchanged when they match the last start");
            port.SetValue(behaviour, currentPort + 1);
            Check(Changed(), "map sees a changed Port");
            port.SetValue(behaviour, currentPort);
            bindIp.SetValue(behaviour, currentBindIp + "x");
            Check(Changed(), "map sees a changed BindIp");
            bindIp.SetValue(behaviour, currentBindIp);
            textureSize.SetValue(behaviour, currentTexture * 2);
            Check(Changed(), "map sees a changed TextureSize");
        }
        finally
        {
            port.SetValue(behaviour, oldPort);
            bindIp.SetValue(behaviour, oldBindIp);
            textureSize.SetValue(behaviour, oldTexture);
        }
    }

    private void TestRegionLabels()
    {
        Type catalog = AccessTools.TypeByName("ValheimOne.LiveMap.BiomeRegionCatalog");
        Type snapshot = AccessTools.TypeByName("ValheimOne.LiveMap.BiomeRegionSnapshot");
        int perBiome = (int)AccessTools.Field(catalog, "MaximumRegionsPerBiome").GetRawConstantValue();
        Check(perBiome == 6, "region labels keep six candidates per biome");
        IList NewCandidates() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(snapshot));
        object Region(string biome, long area) =>
            Activator.CreateInstance(snapshot, biome, biome, 0f, 0f, area);
        string Biome(object region) => (string)ReadMember(region, "Biome")!;
        long Area(object region) => (long)ReadMember(region, "Area")!;

        // Thirty large Mistlands patches used to take every slot ahead of a small swamp.
        IList crowded = NewCandidates();
        for (int i = 0; i < 30; i++) crowded.Add(Region("mistlands", 9_000_000 - i));
        for (int i = 0; i < 3; i++) crowded.Add(Region("swamp", 300_000 - i));
        object[] picked = ((Array)Call(catalog, "SelectRegions", crowded)!).Cast<object>().ToArray();
        Check(picked.Count(r => Biome(r) == "mistlands") == perBiome && picked.Count(r => Biome(r) == "swamp") == 3,
            "a crowded biome cannot push smaller swamps off the region list");
        Check(picked.Zip(picked.Skip(1), (a, b) => Area(a) >= Area(b)).All(x => x),
            "region candidates stay ordered largest first");

        IList many = NewCandidates();
        for (int biome = 0; biome < 12; biome++)
            for (int i = 0; i < 10; i++) many.Add(Region("b" + biome, 1_000_000 - (biome * 10) - i));
        Check(((Array)Call(catalog, "SelectRegions", many)!).Length == 60, "region list keeps its 60-name ceiling");

        // The real catalog on the fixture world, sampled from the live world generator.
        Func<bool> never = () => false;
        object[] world = ((Array)Call(catalog, "Build", WorldGenerator.instance, never)!).Cast<object>().ToArray();
        Dictionary<string, int> counts = world.GroupBy(Biome).ToDictionary(g => g.Key, g => g.Count());
        Logger.LogInfo("REGION LABELS " + string.Join(" ", counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
        Check(world.Length > 0 && world.Length <= 60 && counts.Values.All(c => c <= perBiome),
            "fixture world region list respects the per-biome and total limits");
        Check(counts.ContainsKey("swamp") && counts.ContainsKey("mountain"),
            "fixture world names its swamps and mountains");
    }

    // Valheim 1.0 content the map and modules must know by name (checked on Valheim 1.0.20).
    private void TestValheimOneZeroData()
    {
        var known = new HashSet<string>(
            ZoneSystem.instance.m_locations.Where(l => l != null).Select(l => l.m_prefabName),
            StringComparer.Ordinal);
        (string Name, string Group)[] places =
        {
            ("MorkBorg", "dungeon_morkhalla"),
            ("TheHole01", "dungeon_windingtunnels"),
            ("BearCave", "dungeon_bearcave"),
            ("NorthVillage", "structure_deepnorth"),
            ("DN_hut01", "structure_deepnorth"),
            ("LumberCamp", "structure_deepnorth"),
            ("FrozenShip01_DN", "structure_shipwreck"),
            ("FrozenShip03_DN", "structure_shipwreck"),
            ("NorthMemorialPlace", "structure_runestone"),
            ("GoblinHut02", "structure_camp"),
            ("AncientUpgradeStation", "structure_forge"),
            ("DN_Bossroom", "boss"),
        };
        foreach (var (name, group) in places)
        {
            Check(known.Contains(name), "Valheim 1.0 location exists in this build: " + name);
            Check(PoiClassifier.Classify(name, false, false) == group, name + " is served on " + group);
            Check(PoiGroups.TryGet(group, out _), group + " is a defined map layer");
        }

        string[] dungeons = { "dungeon_bearcave", "dungeon_morkhalla", "dungeon_windingtunnels" };
        string[] labels = { "Bear Cave", "M\u00f6rkhalla", "Winding Tunnels" };
        for (int i = 0; i < dungeons.Length; i++)
            Check((string)Call(typeof(DungeonRegistry), "GetTypeLabel", dungeons[i], "")! == labels[i],
                "dungeon viewer names " + dungeons[i] + " " + labels[i]);

        // The game's own map colours: swamp is a warm brown, Mistlands a cool mauve.
        MapColor swamp = BiomePalette.Get(Heightmap.Biome.Swamp, 40f);
        MapColor mist = BiomePalette.Get(Heightmap.Biome.Mistlands, 40f);
        Check(swamp.Red > swamp.Green && swamp.Green > swamp.Blue, "swamp is drawn red-brown like the game's map");
        Check(mist.Blue > mist.Green && mist.Red > mist.Green, "Mistlands is drawn mauve like the game's map mist");

        // Gathering covers the Deep North wood, ice and ore.
        var gatherSettings = new ValheimOneConfig(Path.Combine(_root, "gathering-overlay.cfg"));
        var gathering = new GatheringModule(gatherSettings.Features);
        Check(gatherSettings.ApplyOverlay(
            "[Gathering] / Enabled=true\n[Gathering] / Timberwood=100\n" +
            "[Gathering] / Ice=100\n[Gathering] / PetrifiedTissue=100\n") == 4,
            "the Deep North gathering settings arrive through the synced overlay");
        var drops = new List<GameObject>();
        foreach (string item in new[] { "Frostwood", "Ice", "GoldOre", "Wood" })
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(item);
            Check(prefab != null, "native material exists: " + item);
            drops.Add(prefab!);
        }
        AccessTools.Method(typeof(GatheringModule), "ScaleMaterialCounts", new[] { typeof(List<GameObject>) })
            .Invoke(gathering, new object[] { drops });
        Check(drops.Count(d => d.name == "Frostwood") == 2 && drops.Count(d => d.name == "Ice") == 2 &&
            drops.Count(d => d.name == "GoldOre") == 2 && drops.Count(d => d.name == "Wood") == 1,
            "Timberwood, Ice and Petrified Tissue drop counts follow their own settings");

        // The Frigid Kiln makes Liquid Frost from Ice and takes no ore.
        var productionSettings = new ValheimOneConfig(Path.Combine(_root, "frigid-overlay.cfg"));
        var production = new ProductionSpeedsModule(productionSettings.Features, new ModLogger(Logger));
        object? previousProduction = Get(typeof(ProductionSpeedsModule), "_active");
        try
        {
            Set(typeof(ProductionSpeedsModule), "_active", production);
            Smelter kiln = NewStation("piece_FrostKiln", new Vector3(820, 600, 700)).GetComponent<Smelter>();
            Check(kiln.m_conversion.Any(c => c.m_from == null && c.m_to != null && c.m_to.name == "FrozenFuel") &&
                kiln.m_fuelItem != null && kiln.m_fuelItem.name == "Ice" && kiln.m_maxOre == 0,
                "the frigid kiln burns ice into liquid frost without ore");
            float seconds = kiln.m_secPerProduct;
            int fuel = kiln.m_maxFuel;
            int perProduct = kiln.m_fuelPerProduct;
            Check(productionSettings.ApplyOverlay(
                "[ProductionSpeeds] / Enabled=true\n[ProductionSpeeds] / FrigidKilnProductionSeconds=7\n" +
                "[ProductionSpeeds] / FrigidKilnMaxFuel=9\n[ProductionSpeeds] / FrigidKilnFuelPerProduct=2\n") == 4,
                "the frigid kiln settings arrive through the synced overlay");
            Check(kiln.m_secPerProduct == 7 && kiln.m_maxFuel == 9 && kiln.m_fuelPerProduct == 2 && kiln.m_maxOre == 0,
                "live frigid kiln settings apply and leave it ore-free");
            productionSettings.ClearOverlay();
            Check(kiln.m_secPerProduct == seconds && kiln.m_maxFuel == fuel && kiln.m_fuelPerProduct == perProduct,
                "disabling production overrides restores the frigid kiln");
        }
        finally { Set(typeof(ProductionSpeedsModule), "_active", previousProduction); }

        // Deep North storage: Grausten chest and wardrobe.
        var containerSettings = new ValheimOneConfig(Path.Combine(_root, "containers-overlay.cfg"));
        var containers = new ContainerSizesModule(containerSettings.Features, new ModLogger(Logger));
        object? previousContainers = Get(typeof(ContainerSizesModule), "_active");
        try
        {
            Set(typeof(ContainerSizesModule), "_active", containers);
            Check(containerSettings.ApplyOverlay(
                "[ContainerSizes] / Enabled=true\n" +
                "[ContainerSizes] / GraustenChestColumns=6\n[ContainerSizes] / GraustenChestRows=7\n" +
                "[ContainerSizes] / WardrobeColumns=4\n[ContainerSizes] / WardrobeRows=12\n") == 5,
                "the Deep North storage settings arrive through the synced overlay");
            (string Prefab, int Columns, int Rows, int VanillaColumns, int VanillaRows)[] chests =
            {
                ("piece_chest_grausten", 6, 7, 8, 5),
                ("piece_chest_warderobe", 4, 12, 5, 10),
            };
            var built = new List<Container>();
            for (int i = 0; i < chests.Length; i++)
            {
                Container chest = NewStation(chests[i].Prefab, new Vector3(840 + i * 5, 600, 700)).GetComponent<Container>();
                built.Add(chest);
                Check(chest.m_width == chests[i].Columns && chest.m_height == chests[i].Rows &&
                    chest.GetInventory().GetWidth() == chests[i].Columns && chest.GetInventory().GetHeight() == chests[i].Rows,
                    chests[i].Prefab + " takes its own grid size");
            }
            containerSettings.ClearOverlay();
            for (int i = 0; i < chests.Length; i++)
                Check(built[i].m_width == chests[i].VanillaColumns && built[i].m_height == chests[i].VanillaRows,
                    "disabling container sizes restores " + chests[i].Prefab + " to its game grid");
        }
        finally { Set(typeof(ContainerSizesModule), "_active", previousContainers); }
    }

    // [Drops] DestructibleDrops shared one ItemData between its copies, so a chest filled from the
    // same DropTable seam stacked the object into itself up to the stack limit and listed it again:
    // minutes per chest with stack-size mods, and doubled slots in the save (0.13.24). Chest loot
    // now stays native, and the extras it still scales are separate items.
    private void TestDropsChestLoot()
    {
        var settings = new ValheimOneConfig(Path.Combine(_root, "drops-overlay.cfg"));
        var module = new ItemDropMultiplierModule(settings.Features);
        object? previous = Get(typeof(ItemDropMultiplierModule), "_active");
        try
        {
            Set(typeof(ItemDropMultiplierModule), "_active", module);
            Check(settings.ApplyOverlay("[Drops] / Enabled=true\n[Drops] / DestructibleDrops=400\n") == 2,
                "destructible drops at 400 percent arrives through the synced overlay");

            string[] prefabs = { "TreasureChest_forestcrypt", "TreasureChest_meadows", "TreasureChest_blackforest", "TreasureChest_mountaincave" };
            int chests = 0, items = 0;
            for (int i = 0; i < 24; i++)
            {
                string prefab = prefabs[i % prefabs.Length];
                Container chest = NewStation(prefab, new Vector3(900 + (i % 6) * 4, 600, 700 + (i / 6) * 4)).GetComponent<Container>();
                List<ItemDrop.ItemData> loot = chest.GetInventory().GetAllItems();
                var slots = new HashSet<Vector2i>();
                bool slotsUnique = loot.All(item => slots.Add(item.m_gridPos));
                Check(loot.Count > 0, prefab + " is filled with Drops on");
                Check(AllDistinct(loot), prefab + " holds no item object twice");
                Check(slotsUnique, prefab + " has one item per slot");
                Check(loot.Count <= chest.m_defaultItems.m_dropMax,
                    prefab + " keeps its native loot count (" + loot.Count + " of at most " + chest.m_defaultItems.m_dropMax + ")");
                Check(loot.All(item => item.m_stack >= 1 && item.m_stack <= item.m_shared.m_maxStackSize),
                    prefab + " stacks stay inside the item's stack limit");
                chests++;
                items += loot.Count;
            }
            Check(chests == 24 && items >= 24, "24 native loot chests filled with " + items + " items");
            CheckScaledExtras("after chests were filled");
            settings.ClearOverlay();
        }
        finally { Set(typeof(ItemDropMultiplierModule), "_active", previous); }
    }

    private void CheckScaledExtras(string when)
    {
        GameObject coins = ObjectDB.instance.GetItemPrefab("Coins");
        var table = new DropTable
        {
            m_drops = new List<DropTable.DropData>
            {
                new DropTable.DropData { m_item = coins, m_stackMin = 7, m_stackMax = 7, m_weight = 1f, m_dontScale = true },
            },
            m_dropMin = 2,
            m_dropMax = 2,
            m_dropChance = 1f,
        };
        var extras = new List<ItemDrop.ItemData>(table.GetDropListItems());
        Check(extras.Count == 10, "pickable and fishing extras still scale " + when + " (2 rolls x 5 = " + extras.Count + ")");
        Check(AllDistinct(extras), "every scaled extra is its own item " + when);
        Check(extras.All(item => item.m_stack == 7 && item.m_dropPrefab == coins), "scaled extras keep the rolled stack " + when);
    }

    private static bool AllDistinct(IList<ItemDrop.ItemData> items)
    {
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                if (ReferenceEquals(items[i], items[j])) return false;
        return true;
    }

    // [Network] (0.13.25). The only send-queue read in the game, inside ZDOMan.SendZDOs, goes
    // through NetworkModule.QueueForSendGate. Run against the real method with a recording
    // socket: the stock rule stops a player's package at 10 KB, the lift lets up to 32 KB
    // through, the switch and the stand-down give back the game's own numbers, PlayFab counts
    // its real bytes in flight, and Steam's send-rate window follows the switch.
    private void TestNetworkSendLimit()
    {
        Check(new ItemDropMultiplierModule(new ValheimOneConfig(Path.Combine(_root, "class-drops.cfg")).Features).Classification == FeatureClassification.Synced,
            "Drops is labelled Synced (rolled in the nearby player's game)");
        Check(new GatheringModule(new ValheimOneConfig(Path.Combine(_root, "class-gathering.cfg")).Features).Classification == FeatureClassification.Synced,
            "Gathering is labelled Synced");
        Check(new WorldEventsModule(new ValheimOneConfig(Path.Combine(_root, "class-events.cfg")).Features).Classification == FeatureClassification.Synced,
            "Events is labelled Synced (guardian powers run in each player's game)");

        Patches? sendPatches = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZDOMan), "SendZDOs"));
        Check(sendPatches != null && sendPatches.Transpilers.Any(p => p.owner == ValheimOnePlugin.PluginGuid),
            "the plugin rewrote the queue read in ZDOMan.SendZDOs");
        Patches? ratePatches = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks"));
        Check(ratePatches != null && ratePatches.Postfixes.Any(p => p.owner == ValheimOnePlugin.PluginGuid),
            "the plugin follows the game's Steam send-rate setup");
        Check(NetworkModule.IsLiftActive(), "the lift is on by default on a dedicated server with no other network mod");
        // The game's own Steam setup (150 KB/s fixed on its first socket), with the plugin's postfix after it.
        AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks").Invoke(null, null);
        Check(SteamRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin) == NetworkModule.LiftedSendRateMin &&
              SteamRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax) == NetworkModule.LiftedSendRateMax,
            "after the game's Steam setup the send-rate window is 256 KB/s to 1 MB/s (game: 150 KB/s fixed)");

        var settings = new ValheimOneConfig(Path.Combine(_root, "network-overlay.cfg"));
        var module = new NetworkModule(settings.Features, new ModLogger(Logger));
        Check(module.Classification == FeatureClassification.ServerAuthoritative && module.Section == "Network",
            "[Network] is a server-side section that works for every player");
        object? previous = Get(typeof(NetworkModule), "_active");
        var zdos = new List<ZDO>();
        var foreign = new Harmony("test.foreign.sendqueue");
        var devcommands = new Harmony(NetworkModule.ServerDevcommandsGuid);
        try
        {
            Set(typeof(NetworkModule), "_active", module);
            Set(module, "_patched", true);
            Set(module, "_resolved", true);
            Set(module, "_standDownReason", null);

            Check(NetworkModule.QueueForSendGate(new RecordingSocket { Queue = 0 }) == -22528 &&
                  NetworkModule.QueueForSendGate(new RecordingSocket { Queue = 15000 }) == -7528 &&
                  NetworkModule.QueueForSendGate(new RecordingSocket { Queue = 40000 }) == 17472,
                "Steam queue is counted against a 32 KB window (22,528 bytes low)");

            Check(PlayFabGate(30000) == 7472 && PlayFabGate(50000) == 12500 && PlayFabGate(10000) == -12528,
                "PlayFab counts real bytes in flight against 32 KB, never above the game's own quarter-scaled figure");

            // A cluster of objects one player must receive: about 49 KB of world data.
            var center = new Vector3(5120f, 30f, 5120f);
            byte[] fill = new byte[100];
            for (int i = 0; i < 300; i++)
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(center + new Vector3(i % 20, 0f, i / 20), "Wood".GetStableHashCode());
                zdo.Set("vo_network_fill", fill);
                zdos.Add(zdo);
            }

            int lifted = SendOnce(center, 0, out bool sentLifted);
            Check(sentLifted && lifted > 12000 && lifted < 33600,
                "with the lift one player's package reaches past 10 KB up to 32 KB (" + lifted + " bytes)");
            int liftedBusy = SendOnce(center, 11000, out bool sentBusy);
            Check(sentBusy && liftedBusy > 10240 && liftedBusy < 22500,
                "with 11 KB already on the way the lift still sends the rest of 32 KB (" + liftedBusy + " bytes)");
            SendOnce(center, 31000, out bool sentFull);
            Check(!sentFull, "with 31 KB on the way the lift sends nothing, like the game at 10 KB");

            Check(settings.ApplyOverlay("[Network] / Enabled=false\n") == 1 && !NetworkModule.IsLiftActive(),
                "the switch turns the lift off live");
            int stock = SendOnce(center, 0, out bool sentStock);
            Check(sentStock && stock > 8000 && stock < 10700, "switched off, the package stops at the game's 10 KB (" + stock + " bytes)");
            SendOnce(center, 11000, out bool sentStockBusy);
            Check(!sentStockBusy, "switched off, 11 KB on the way blocks the player as in the game");
            Check(NetworkModule.QueueForSendGate(new RecordingSocket { Queue = 4321 }) == 4321, "switched off, the game's queue figure passes through");
            Call(typeof(NetworkModule), "ApplySteamSendRates");
            Check(SteamRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin) == NetworkModule.VanillaSendRate &&
                  SteamRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax) == NetworkModule.VanillaSendRate,
                "switched off live, Steam's send rate goes back to the game's 150 KB/s");
            settings.ClearOverlay();
            Call(typeof(NetworkModule), "ApplySteamSendRates");
            Check(NetworkModule.IsLiftActive() &&
                  SteamRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax) == NetworkModule.LiftedSendRateMax,
                "switched back on live, the lift and the send-rate window return");

            // Stand-down: another mod that changes the queue, then Better Networking by its GUID.
            foreign.Patch(AccessTools.Method(typeof(ZSteamSocket), "GetSendQueueSize"),
                postfix: new HarmonyMethod(typeof(RuntimeRegression), nameof(ForeignQueuePostfix)));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains("test.foreign.sendqueue"),
                "a mod that patches the queue makes the lift stand down and is named");
            Check(NetworkModule.QueueForSendGate(new RecordingSocket { Queue = 5000 }) == 5000, "standing down, the game's figure passes through");
            foreign.UnpatchSelf();
            Set(module, "_resolved", false);
            Set(module, "_standDownReason", null);
            Check(NetworkModule.IsLiftActive(), "the lift returns once no other mod changes the queue");
            foreign.Patch(AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks"),
                postfix: new HarmonyMethod(typeof(RuntimeRegression), nameof(ForeignRatePostfix)));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains("test.foreign.sendqueue"),
                "a mod that sets Steam's send rate makes the lift stand down too");
            foreign.UnpatchSelf();
            Set(module, "_resolved", false);
            Set(module, "_standDownReason", null);

            // Server Devcommands' ghost-mode patches on SendZDOs only look on (a void prefix and postfix).
            MethodInfo sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            devcommands.Patch(sendZdos,
                prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(ObserverPrefix)),
                postfix: new HarmonyMethod(typeof(RuntimeRegression), nameof(ObserverPostfix)));
            Set(module, "_resolved", false);
            Check(NetworkModule.IsLiftActive() && NetworkModule.StandDownReason == null,
                "Server Devcommands' look-only prefix and postfix on SendZDOs leave the lift on");
            int beside = SendOnce(center, 0, out bool sentBeside);
            Check(sentBeside && beside > 12000, "beside Server Devcommands the package still reaches past 10 KB (" + beside + " bytes)");
            devcommands.Patch(sendZdos, prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(SkippingPrefix)));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains(NetworkModule.ServerDevcommandsGuid),
                "a Server Devcommands prefix that could skip SendZDOs still makes the lift stand down");
            devcommands.UnpatchSelf();
            Set(module, "_resolved", false);
            Set(module, "_standDownReason", null);
            foreign.Patch(sendZdos, prefix: new HarmonyMethod(typeof(RuntimeRegression), nameof(ObserverPrefix)));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains("test.foreign.sendqueue"),
                "the look-only exception is Server Devcommands' alone: any other mod on SendZDOs still counts");
            foreign.UnpatchSelf();
            Set(module, "_resolved", false);
            Set(module, "_standDownReason", null);
            Chainloader.PluginInfos["DIT.BetterNetworking10"] =
                (PluginInfo)FormatterServices.GetUninitializedObject(typeof(PluginInfo));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains("Better Networking 1.0"),
                "the Better Networking 1.0 fork by its GUID makes the lift stand down");
            Chainloader.PluginInfos.Remove("DIT.BetterNetworking10");
            Set(module, "_resolved", false);
            Set(module, "_standDownReason", null);
            Chainloader.PluginInfos[NetworkModule.BetterNetworkingGuid] =
                (PluginInfo)FormatterServices.GetUninitializedObject(typeof(PluginInfo));
            Set(module, "_resolved", false);
            Check(!NetworkModule.IsLiftActive() && (NetworkModule.StandDownReason ?? "").Contains("Better Networking"),
                "Better Networking by its GUID makes the lift stand down");
            int bn = SendOnce(center, 0, out bool sentBn);
            Check(sentBn && bn < 10700, "standing down, the package stops at the game's 10 KB (" + bn + " bytes)");
        }
        finally
        {
            Chainloader.PluginInfos.Remove(NetworkModule.BetterNetworkingGuid);
            Chainloader.PluginInfos.Remove("DIT.BetterNetworking10");
            foreign.UnpatchSelf();
            devcommands.UnpatchSelf();
            settings.ClearOverlay();
            Set(typeof(NetworkModule), "_active", previous);
            Call(typeof(NetworkModule), "ApplySteamSendRates");
            foreach (ZDO zdo in zdos) ZDOMan.instance.DestroyZDO(zdo);
        }
    }

    // Pins, heatmap, timelapse and activity belong to one world (0.13.26). The first world on the new version gets a
    // copy of the old shared files, they stay where they were, and any later world starts empty.
    private void TestWorldData()
    {
        string root = Path.Combine(_root, "world-data");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "timelapse", "older"));
        var legacy = new Dictionary<string, string>
        {
            ["webpins.json"] = "{\"pins\":[{\"label\":\"old base\"}]}",
            ["heatmap.json"] = "{\"cells\":[1,2,3]}",
            ["ghosts.json"] = "[]",
            ["console-history.json"] = "[\"save\"]",
            ["activity-2026-10-20.jsonl"] = "{\"type\":\"join\"}\n",
            ["activity-2026-10-21.jsonl"] = "{\"type\":\"leave\"}\n",
            [Path.Combine("timelapse", "1760000000000.vof")] = "frame-a",
            [Path.Combine("timelapse", "older", "1750000000000.vof")] = "frame-b",
            ["leaderboards.json"] = "{\"seed\":1}",
        };
        foreach (KeyValuePair<string, string> file in legacy) File.WriteAllText(Path.Combine(root, file.Key), file.Value);
        Directory.CreateDirectory(Path.Combine(root, "worlds", "Saga_One.copying"));
        File.WriteAllText(Path.Combine(root, "worlds", "Saga_One.copying", "partial.json"), "half");

        string first = WorldDataDirectory.Resolve(root, "Saga One", new ModLogger(Logger));
        Check(first == Path.Combine(root, "worlds", "Saga_One"), "the first world gets its own folder named like its map cache");
        bool copiedAll = legacy.Where(f => f.Key != "leaderboards.json")
            .All(f => File.Exists(Path.Combine(first, f.Key)) && File.ReadAllText(Path.Combine(first, f.Key)) == f.Value);
        Check(copiedAll, "pins, heatmap, offline players, console history, activity and timelapse are copied into it");
        Check(legacy.All(f => File.Exists(Path.Combine(root, f.Key)) && File.ReadAllText(Path.Combine(root, f.Key)) == f.Value),
            "the old shared files stay where they were (copied, never moved)");
        Check(!File.Exists(Path.Combine(first, "leaderboards.json")), "the seed-checked leaderboard stays shared");
        Check(!Directory.Exists(Path.Combine(root, "worlds", "Saga_One.copying")) && !File.Exists(Path.Combine(first, "partial.json")),
            "a half-finished copy from an earlier crash is thrown away, never taken as done");
        string marker = File.ReadAllText(Path.Combine(root, "worlds", WorldDataDirectory.LegacyMarkerFile));
        Check(marker.Contains("world=Saga_One") && marker.Contains("copied_files=8"), "the marker names the world that got the copy");

        File.WriteAllText(Path.Combine(first, "webpins.json"), "{\"pins\":[]}");
        string second = WorldDataDirectory.Resolve(root, "Other World", null);
        Check(second == Path.Combine(root, "worlds", "Other_World") && Directory.GetFileSystemEntries(second).Length == 0,
            "a world switched to later starts empty instead of showing the old world's pins");
        string again = WorldDataDirectory.Resolve(root, "Saga One", null);
        Check(again == first && File.ReadAllText(Path.Combine(first, "webpins.json")) == "{\"pins\":[]}",
            "coming back to the first world keeps its own data and copies nothing again");

        Check(WorldDataDirectory.ReadWorldName(new[] { "valheim_server", "-name", "x", "-world", "Midgard" }) == "Midgard" &&
              WorldDataDirectory.ReadWorldName(new[] { "valheim_server", "-WORLD", "" }) == "Dedicated" &&
              WorldDataDirectory.ReadWorldName(new string[0]) == "Dedicated",
            "the world name comes from -world as the game reads it, else the game's own Dedicated");
        Check(WorldDataDirectory.SanitizeWorldName("../..") == ".._.." && WorldDataDirectory.SanitizeWorldName("") == "world" &&
              WorldDataDirectory.SanitizeWorldName("a/b\\c:d") == "a_b_c_d",
            "a world name cannot leave the worlds folder");

        // The running plugin on the regression world.
        string world = WorldDataDirectory.SanitizeWorldName(WorldDataDirectory.ReadWorldName(Environment.GetCommandLineArgs()));
        string expected = Path.Combine(Paths.ConfigPath, "ValheimOne", "worlds", world);
        object plugin = Chainloader.PluginInfos[ValheimOnePlugin.PluginGuid].Instance;
        object liveMap = Get(plugin, "_liveMapModule")!;
        object activity = Get(plugin, "_activityLogModule")!;
        Check((string)Get(liveMap, "_worldDataDirectory")! == expected, "the live map keeps pins, heatmap and timelapse in " + world + "'s folder");
        Check((string)ReadMember(activity, "DataDirectory")! == expected, "the activity log writes into " + world + "'s folder");
        Check((string)Get(liveMap, "_dataDirectory")! == Path.Combine(Paths.ConfigPath, "ValheimOne"), "the item catalog cache stays shared");
    }

    // Live event streams are counted per view (0.13.26): public and shared viewers can no longer take the owner's slots.
    private void TestEventStreamSlots()
    {
        var slots = new EventStreamSlots();
        int Fill(EventStreamView view) { int n = 0; while (n < 50 && slots.TryAcquire(view)) n++; return n; }
        Check(Fill(EventStreamView.Public) == 2 && Fill(EventStreamView.Shared) == 3,
            "public streams stop at 2 and shared at 3");
        Check(Fill(EventStreamView.Admin) == 6, "the owner's admin view still gets all 6 of its own streams");
        slots.Release(EventStreamView.Public);
        Check(slots.TryAcquire(EventStreamView.Public) && !slots.TryAcquire(EventStreamView.Public),
            "a closed public stream frees exactly one public slot");
        for (int i = 0; i < 10; i++) slots.Release(EventStreamView.Shared);
        Check(slots.Count(EventStreamView.Shared) == 0 && Fill(EventStreamView.Shared) == 3,
            "extra releases never push a count below zero");
    }

    private void LiveResult(bool ok, string name)
    {
        lock (_liveResults) _liveResults.Add((ok, name));
    }

    private void RunLiveEventStreamChecks()
    {
        try
        {
            string root = Environment.GetEnvironmentVariable("CONSOLE_PROBE_URL") ?? "http://127.0.0.1:24583";
            string token = Environment.GetEnvironmentVariable("CONSOLE_PROBE_TOKEN") ?? "";
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            EventStreamSlots? slots = null;
            while (DateTime.UtcNow < deadline)
            {
                slots = RunningEventStreamSlots();
                if (slots != null && StatusAnswers(root, token)) break;
                slots = null;
                Thread.Sleep(200);
            }

            if (slots == null)
            {
                LiveResult(false, "the live map answered for the event-stream check");
                return;
            }

            int publicTaken = FillSlots(slots, EventStreamView.Public);
            int sharedTaken = FillSlots(slots, EventStreamView.Shared);
            try
            {
                LiveResult(publicTaken == EventStreamSlots.PublicLimit && sharedTaken == EventStreamSlots.SharedLimit,
                    "the running map's public and shared slots fill at 2 and 3");
                int code = OpenEventStream(root, token, out string first);
                LiveResult(code == 200 && first.StartsWith("retry:", StringComparison.Ordinal),
                    "with every public and shared slot taken, the owner's map still gets its live stream (HTTP " + code + ")");
            }
            finally
            {
                for (int i = 0; i < publicTaken; i++) slots.Release(EventStreamView.Public);
                for (int i = 0; i < sharedTaken; i++) slots.Release(EventStreamView.Shared);
            }

            int adminTaken = FillSlots(slots, EventStreamView.Admin);
            try
            {
                int code = OpenEventStream(root, token, out _);
                LiveResult(code == 409, "with every admin slot taken, one more admin stream is told to keep polling (HTTP " + code + ")");
            }
            finally
            {
                for (int i = 0; i < adminTaken; i++) slots.Release(EventStreamView.Admin);
            }
        }
        catch (Exception exception)
        {
            LiveResult(false, "live event-stream check: " + exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            _liveDone = true;
        }
    }

    private static int FillSlots(EventStreamSlots slots, EventStreamView view)
    {
        int taken = 0;
        while (taken < 50 && slots.TryAcquire(view)) taken++;
        return taken;
    }

    // Plain reference reads only: this runs off the main thread.
    private static EventStreamSlots? RunningEventStreamSlots()
    {
        Type behaviourType = typeof(ValheimOnePlugin).Assembly.GetType("ValheimOne.LiveMap.LiveMapBehaviour", true)!;
        object? behaviour = AccessTools.Property(behaviourType, "Instance").GetValue(null, null);
        if (behaviour is null) return null;
        object? server = AccessTools.Field(behaviourType, "_httpServer").GetValue(behaviour);
        return server is null ? null : AccessTools.Field(server.GetType(), "_eventStreams").GetValue(server) as EventStreamSlots;
    }

    private static bool StatusAnswers(string root, string token)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(root + "/api/status");
            request.Headers["X-LiveMap-Token"] = token;
            request.Timeout = 2000;
            request.KeepAlive = false;
            using (request.GetResponse()) { }
            return true;
        }
        catch (WebException)
        {
            return false;
        }
    }

    private static int OpenEventStream(string root, string token, out string firstLine)
    {
        firstLine = "";
        var request = (HttpWebRequest)WebRequest.Create(root + "/api/events");
        request.Headers["X-LiveMap-Token"] = token;
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.KeepAlive = false;
        try
        {
            var response = (HttpWebResponse)request.GetResponse();
            int code = (int)response.StatusCode;
            var reader = new StreamReader(response.GetResponseStream());
            firstLine = reader.ReadLine() ?? "";
            request.Abort();
            return code;
        }
        catch (WebException exception) when (exception.Response is HttpWebResponse refused)
        {
            int code = (int)refused.StatusCode;
            refused.Close();
            return code;
        }
    }

    private static void ObserverPrefix() { }

    private static void ObserverPostfix() { }

    private static bool SkippingPrefix() => true;

    private static void ForeignQueuePostfix(ref int __result) { }

    private static void ForeignRatePostfix() { }

    private static int PlayFabGate(int bytesInFlight)
    {
        var socket = (ZPlayFabSocket)FormatterServices.GetUninitializedObject(typeof(ZPlayFabSocket));
        var queue = new ZPlayFabSocket.InFlightQueue();
        queue.Enqueue(new byte[bytesInFlight]);
        Set(socket, "m_inFlightQueue", queue);
        return NetworkModule.QueueForSendGate(socket);
    }

    // One call of the real ZDOMan.SendZDOs for a fresh player standing at center whose socket
    // reports `queue` bytes already on the way; returns the size of the package it sent.
    private int SendOnce(Vector3 center, int queue, out bool sent)
    {
        var socket = new RecordingSocket { Queue = queue };
        var netPeer = new ZNetPeer(socket, server: false)
        {
            m_uid = 424242,
            m_refPos = center,
            m_simulationDistance = SimulationDistance.OriginalDistance,
        };
        object peer = Activator.CreateInstance(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), nonPublic: true);
        Set(peer, "m_peer", netPeer);
        sent = (bool)AccessTools.Method(typeof(ZDOMan), "SendZDOs").Invoke(ZDOMan.instance, new[] { peer, (object)false });
        Check(sent == (socket.Sent.Count == 1), "SendZDOs reports exactly what it put on the socket");
        return socket.Sent.Count == 0 ? 0 : socket.Sent[0];
    }

    private static int SteamRate(ESteamNetworkingConfigValue key)
    {
        ulong size = 4;
        var type = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32;
        GCHandle handle = GCHandle.Alloc(new byte[4], GCHandleType.Pinned);
        try
        {
            ESteamNetworkingGetConfigValueResult result = SteamGameServerNetworkingUtils.GetConfigValue(
                key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out type, handle.AddrOfPinnedObject(), ref size);
            return result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK ||
                   result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited
                ? Marshal.ReadInt32(handle.AddrOfPinnedObject())
                : -1;
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class RecordingSocket : ISocket
    {
        public int Queue;
        public readonly List<int> Sent = new();
        public bool IsConnected() => true;
        public void Send(ZPackage pkg) => Sent.Add(pkg.Size());
        public ZPackage Recv() => null!;
        public int GetSendQueueSize() => Queue;
        public int GetCurrentSendRate() => 0;
        public bool IsHost() => false;
        public void Dispose() { }
        public bool GotNewData() => false;
        public void Close() { }
        public string GetEndPointString() => "recording";
        public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
        public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
        { localQuality = remoteQuality = 1f; ping = 0; outByteSec = inByteSec = 0f; }
        public ISocket Accept() => null!;
        public int GetHostPort() => 0;
        public bool Flush() => true;
        public string GetHostName() => "recording";
        public void VersionMatch() { }
    }

    private static object? Call(object target, string name, params object[] args) =>
        AccessTools.Method(target as Type ?? target.GetType(), name).Invoke(target is Type ? null : target, args);
}

// Opt-in spatial fixture for browser checks. It records known coordinates using
// the real fog tracker and must only run in a disposable test world.
[BepInPlugin("com.humangenome.valheimone.foggeometry", "ValheimOne fog geometry probe", "1.0.0")]
[BepInDependency(ValheimOnePlugin.PluginGuid)]
public sealed class FogGeometryProbe : BaseUnityPlugin
{
    private bool _done;
    private float _next;

    private void Awake()
    {
        enabled = Environment.GetEnvironmentVariable("VALHEIMONE_FOG_GEOMETRY") == "1";
    }

    private void Update()
    {
        if (_done || Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + 0.5f;
        Type behaviourType = typeof(ValheimOnePlugin).Assembly.GetType("ValheimOne.LiveMap.LiveMapBehaviour", true)!;
        var behaviour = AccessTools.Property(behaviourType, "Instance").GetValue(null, null) as UnityEngine.Object;
        if (behaviour == null) return;
        object? server = AccessTools.Field(behaviourType, "_httpServer").GetValue(behaviour);
        if (server == null || !(bool)AccessTools.Property(server.GetType(), "IsRunning").GetValue(server, null)) return;
        object? tracker = AccessTools.Field(behaviourType, "_fogTracker").GetValue(behaviour);
        if (tracker == null) return;
        Type type = tracker.GetType();
        AccessTools.Method(type, "Stamp").Invoke(tracker, new object[] { 3000f, -3000f });
        AccessTools.Method(type, "Stamp").Invoke(tracker, new object[] { -6120f, 4632f });
        AccessTools.Method(type, "MarkChanged").Invoke(tracker, Array.Empty<object>());
        AccessTools.Method(type, "PublishPending").Invoke(tracker, new object[] { true });
        string directory = Path.Combine(Paths.ConfigPath, "runtime-regression");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "fog-geometry-ready.json"),
            "{\"worldSpan\":24576,\"points\":[{\"x\":3000,\"z\":-3000},{\"x\":-6120,\"z\":4632}]}");
        Logger.LogInfo("FOG GEOMETRY READY: known world coordinates recorded in the native tracker.");
        _done = true;
    }
}
