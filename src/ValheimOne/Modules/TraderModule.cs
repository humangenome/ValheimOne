using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class TraderModule : IFeatureModule
{
    private static TraderModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _buyPriceMultiplier;
    private readonly List<TraderBaseline> _baselines = new List<TraderBaseline>();

    public TraderModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _buyPriceMultiplier = _feature.Percent(
            "BuyPriceMultiplier",
            0f,
            "Coin prices of all items sold by traders. Adjusted prices are rounded and clamped to at least one coin.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Trader prices";

    public string Section => "Trader";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // StoreGui reads the Trader's stock and prices on the interacting client. Synced settings
        // keep that client-side buy UI consistent with the server-selected configuration.
        _active = this;

        var traderStart = AccessTools.Method(typeof(Trader), "Start", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Trader), "Start");
        harmony.Patch(
            traderStart,
            postfix: new HarmonyMethod(typeof(TraderModule), nameof(TraderStartPostfix)));
    }

    private static void TraderStartPostfix(Trader __instance)
    {
        TraderModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToTrader(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Trader[] traders = UnityEngine.Object.FindObjectsOfType<Trader>();
#pragma warning restore CS0618
        foreach (Trader trader in traders)
        {
            TraderBaseline? baseline = FindBaseline(trader);
            if (IsEnabled)
            {
                ApplyToTrader(trader, baseline ?? AddBaseline(trader));
            }
            else
            {
                baseline?.Restore();
            }
        }
    }

    private TraderBaseline GetOrAddBaseline(Trader trader)
    {
        return FindBaseline(trader) ?? AddBaseline(trader);
    }

    private TraderBaseline? FindBaseline(Trader trader)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            TraderBaseline baseline = _baselines[index];
            if (!baseline.Trader.TryGetTarget(out Trader? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, trader))
            {
                return baseline;
            }
        }

        return null;
    }

    private TraderBaseline AddBaseline(Trader trader)
    {
        var baseline = new TraderBaseline(trader);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToTrader(Trader trader, TraderBaseline baseline)
    {
        if (!baseline.Trader.TryGetTarget(out Trader? existing) ||
            existing == null ||
            !ReferenceEquals(existing, trader))
        {
            return;
        }

        foreach (TradeItemBaseline item in baseline.Items)
        {
            item.Item.m_price = ScalePrice(item.Price);
        }
    }

    private int ScalePrice(int basePrice)
    {
        float adjustedPrice = _buyPriceMultiplier.Apply(basePrice);
        if (float.IsNaN(adjustedPrice))
        {
            return Math.Max(1, basePrice);
        }

        if (adjustedPrice >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(
            1,
            (int)Math.Round(adjustedPrice, MidpointRounding.AwayFromZero));
    }

    private sealed class TraderBaseline
    {
        public TraderBaseline(Trader trader)
        {
            Trader = new WeakReference<Trader>(trader);
            Items = new List<TradeItemBaseline>(trader.m_items.Count);
            foreach (Trader.TradeItem item in trader.m_items)
            {
                if (item != null)
                {
                    Items.Add(new TradeItemBaseline(item));
                }
            }
        }

        public WeakReference<Trader> Trader { get; }

        public List<TradeItemBaseline> Items { get; }

        public void Restore()
        {
            foreach (TradeItemBaseline item in Items)
            {
                item.Item.m_price = item.Price;
            }
        }
    }

    private sealed class TradeItemBaseline
    {
        public TradeItemBaseline(Trader.TradeItem item)
        {
            Item = item;
            Price = item.m_price;
        }

        public Trader.TradeItem Item { get; }

        public int Price { get; }
    }
}
