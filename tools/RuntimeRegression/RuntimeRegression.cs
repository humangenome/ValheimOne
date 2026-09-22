using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
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
        if (_ran || ZNet.instance == null || !ZNet.instance.IsServer() ||
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
            string result = $"RUNTIME REGRESSION PASS assertions={_assertions} game={(global::Version.GetVersionString())}";
            Logger.LogInfo(result);
            File.WriteAllText(Path.Combine(_root, "result.txt"), result + "\n");
        }
        catch (Exception exception)
        {
            Logger.LogError("RUNTIME REGRESSION FAIL " + exception);
            File.WriteAllText(Path.Combine(_root, "result.txt"), "FAIL " + exception + "\n");
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
