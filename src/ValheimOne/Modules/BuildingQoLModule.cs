using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class BuildingQoLModule : IFeatureModule
{
    private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus>
        PlacementStatusField =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
    private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostField =
        AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
    private static readonly AccessTools.FieldRef<Player, int> PlaceRotationField =
        AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
    private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegreesField =
        AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");

    private static BuildingQoLModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _noStructuralSupport;
    private readonly ConfigEntryBool _noPlacementBlocking;
    private readonly ConfigEntryFloat _maxPlacementDistance;
    private readonly ConfigEntryFloat _freeRotationStepDegrees;
    private readonly List<PlayerBuildingBaseline> _playerBaselines =
        new List<PlayerBuildingBaseline>();

    public BuildingQoLModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _noStructuralSupport = _feature.Bool(
            "NoStructuralSupport",
            defaultValue: false,
            "Treat every structural-support check as supported, allowing pieces to remain in free air.");
        _noPlacementBlocking = _feature.Bool(
            "NoPlacementBlocking",
            defaultValue: false,
            "Allow overlapping and otherwise invalid placement. No-build zones (mystical forces), wards, teleport-only areas, and other location or progression restrictions remain enforced.");
        _maxPlacementDistance = _feature.Float(
            "MaxPlacementDistance",
            0f,
            "Absolute hammer build reach. Zero disables this override; Valheim's default is 8.");
        _freeRotationStepDegrees = _feature.Float(
            "FreeRotationStepDegrees",
            0f,
            "Absolute placement rotation step in degrees. Zero disables this override; Valheim's default is 22.5.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Building quality of life";

    public string Section => "Building";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Placement validation and final placement run on the building client. Keep this feature
        // Synced so every participating builder applies the server's effective configuration.
        _active = this;

        var haveSupport = AccessTools.Method(typeof(WearNTear), "HaveSupport", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(WearNTear), "HaveSupport");
        harmony.Patch(
            haveSupport,
            postfix: new HarmonyMethod(typeof(BuildingQoLModule), nameof(HaveSupportPostfix)));

        var playerAwake = AccessTools.Method(typeof(Player), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), "Awake");
        harmony.Patch(
            playerAwake,
            postfix: new HarmonyMethod(typeof(BuildingQoLModule), nameof(PlayerAwakePostfix)));

        var updatePlacementGhost = AccessTools.Method(
            typeof(Player),
            "UpdatePlacementGhost",
            new[] { typeof(bool) })
            ?? throw new MissingMethodException(nameof(Player), "UpdatePlacementGhost");
        harmony.Patch(
            updatePlacementGhost,
            postfix: new HarmonyMethod(
                typeof(BuildingQoLModule),
                nameof(UpdatePlacementGhostPostfix)));
    }

    private static void HaveSupportPostfix(ref bool __result)
    {
        BuildingQoLModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (active._noStructuralSupport.Value)
        {
            __result = true;
        }
    }

    private static void PlayerAwakePostfix(Player __instance)
    {
        BuildingQoLModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capture untouched values while disabled so a server overlay can hot-enable this module
        // after Awake and later restore the same Player instance.
        PlayerBuildingBaseline baseline = active.GetOrAddBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToPlayer(__instance, baseline);
    }

    private static void UpdatePlacementGhostPostfix(Player __instance)
    {
        BuildingQoLModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        GameObject? placementGhost = PlacementGhostField(__instance);
        if (active._noPlacementBlocking.Value)
        {
            Player.PlacementStatus status = PlacementStatusField(__instance);
            if (IsSuppressiblePlacementStatus(status))
            {
                PlacementStatusField(__instance) = Player.PlacementStatus.Valid;

                // Piece exposes the same public highlight API used by Player's private tint helper.
                Piece? ghostPiece = placementGhost == null
                    ? null
                    : placementGhost.GetComponent<Piece>();
                ghostPiece?.SetInvalidPlacementHeightlight(false);
            }
        }

        float rotationStep = active._freeRotationStepDegrees.Value;
        if (rotationStep <= 0f || placementGhost == null)
        {
            return;
        }

        // m_placeRotationDegrees is also baseline-managed because vanilla uses it for snap-point
        // positioning before this postfix applies the final ghost rotation.
        PlaceRotationDegreesField(__instance) = rotationStep;
        placementGhost.transform.rotation = Quaternion.Euler(
            0f,
            rotationStep * PlaceRotationField(__instance),
            0f);
    }

    private static bool IsSuppressiblePlacementStatus(Player.PlacementStatus status)
    {
        switch (status)
        {
            // These are vanilla's generic geometry/clipping, player-overlap, and spacing blocks.
            case Player.PlacementStatus.Invalid:
            case Player.PlacementStatus.BlockedbyPlayer:
            case Player.PlacementStatus.MoreSpace:
                return true;

            // Preserve Valid, NoBuildZone, PrivateZone, NoTeleportArea,
            // ExtensionMissingStation, WrongBiome, NeedCultivated, NeedDirt, NotInDungeon, and
            // NoRayHits. In particular, mystical-forces zones and ward access remain enforced.
            default:
                return false;
        }
    }

    private void OnEffectiveValuesChanged()
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerBuildingBaseline baseline = _playerBaselines[index];
            if (!baseline.Player.TryGetTarget(out Player? player) || player == null)
            {
                _playerBaselines.RemoveAt(index);
                continue;
            }

            if (IsEnabled)
            {
                ApplyToPlayer(player, baseline);
            }
            else
            {
                baseline.Restore(player);
            }
        }
    }

    private PlayerBuildingBaseline GetOrAddBaseline(Player player)
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerBuildingBaseline baseline = _playerBaselines[index];
            if (!baseline.Player.TryGetTarget(out Player? existingPlayer) || existingPlayer == null)
            {
                _playerBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existingPlayer, player))
            {
                return baseline;
            }
        }

        var added = new PlayerBuildingBaseline(player);
        _playerBaselines.Add(added);
        return added;
    }

    private void ApplyToPlayer(Player player, PlayerBuildingBaseline baseline)
    {
        float placementDistance = _maxPlacementDistance.Value;
        player.m_maxPlaceDistance = placementDistance > 0f
            ? placementDistance
            : baseline.MaxPlacementDistance;

        float rotationStep = _freeRotationStepDegrees.Value;
        PlaceRotationDegreesField(player) = rotationStep > 0f
            ? rotationStep
            : baseline.PlaceRotationDegrees;
    }

    private sealed class PlayerBuildingBaseline
    {
        public PlayerBuildingBaseline(Player player)
        {
            Player = new WeakReference<Player>(player);
            MaxPlacementDistance = player.m_maxPlaceDistance;
            PlaceRotationDegrees = PlaceRotationDegreesField(player);
        }

        public WeakReference<Player> Player { get; }

        public float MaxPlacementDistance { get; }

        public float PlaceRotationDegrees { get; }

        public void Restore(Player player)
        {
            player.m_maxPlaceDistance = MaxPlacementDistance;
            PlaceRotationDegreesField(player) = PlaceRotationDegrees;
        }
    }
}
