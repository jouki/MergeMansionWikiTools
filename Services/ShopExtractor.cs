using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLogic.Config;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Models;
using Metaplay.Core;
using Metaplay.Core.Math;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Extracts the Coins/Gems shop tables straight from the imported <see cref="SharedGameConfig"/>
/// into <c>shop_items.json</c> — the flash sale shops (every event's rotating six slots plus the
/// garage one) and the permanent <c>ShopItems</c> rows.
/// <para>
/// <b>Why this is not a dump engine output.</b> Neither table is produced by any of the six dumpers,
/// and the Events dump's own <c>"Shops"</c> section is a different library (<c>ShopEvents</c>, the
/// IAP event shops), which the 26.07.01 archive does not even contain — so a dump with the Shops
/// filter ticked used to write a bare <c>"Shops": []</c>. Emitting these libraries through an engine
/// instead would break the byte-for-byte Legacy/Native parity the engines are verified with, so the
/// app reads them off the config here, exactly as it does for <c>daily_scoop.json</c>
/// (<see cref="DailyScoopExtractor"/>). The file is identical under both engines.
/// </para>
/// <para>
/// <b>Why reflection.</b> Everything worth having is a private <c>[MetaMember]</c>:
/// <c>FlashSaleDefinition.ItemCosts</c> (the price ladder), <c>BoardShopItem.BoardId</c>/
/// <c>PriceCurve</c>/<c>PurchaseLimiter</c>, and each curve's own <c>Currency</c>/<c>Price</c>.
/// <see cref="MetaObjectWriter"/> walks those the same way the dumpers do.
/// </para>
/// <para>
/// <b>Flash sale pricing.</b> A flash sale slot has a stock (<c>Quantity</c>, shown in game as
/// "N Left") and a <c>ItemCosts</c> ladder, one price per purchase — the price rises with every copy
/// bought, not with time. A ladder shorter than the stock keeps its last price for the rest.
/// Verified against the game on 2026-09-17 (Murder at the Mansion: 800 coins / 26 / 13 / 2 / 3 / 37
/// gems in slot order, refresh for 20 gems).
/// </para>
/// </summary>
public static class ShopExtractor
{
    /// <summary>File name written next to events.json in the dump folder.</summary>
    public const string FileName = "shop_items.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the model from <paramref name="config"/>.</summary>
    public static ShopDump Extract(SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dump = new ShopDump();

        // Offers first: the slots below reference them by id, and a group pointing at an offer that
        // is not in either library is worth a warning rather than a silent gap.
        CollectOffers(config.GarageFlashSales, "Garage", dump);
        CollectOffers(config.EventFlashSales, "Event", dump);

        var groups = CollectGroups(config, dump);
        CollectShops(config, groups, dump);
        CollectShopItems(config, dump);

        return dump;
    }

    /// <summary>Extracts and writes <c>shop_items.json</c>; returns the path.</summary>
    public static string Write(SharedGameConfig config, string outputPath)
    {
        var json = JsonSerializer.Serialize(Extract(config), JsonOpts);
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        return outputPath;
    }

    /// <summary>
    /// Reads a previously written <c>shop_items.json</c>; null when it is missing or unreadable, so
    /// a consumer facing a dump from before this file existed can say so instead of throwing.
    /// </summary>
    public static ShopDump? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ShopDump>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[Shop] {path} could not be read: {ex.Message}");
            return null;
        }
    }

    // ── flash sales ───────────────────────────────────────────────────

    private static void CollectOffers(object? library, string scope, ShopDump dump)
    {
        foreach (var (id, def) in Entries(library))
        {
            var offer = new ShopDump.FlashSaleOfferEntry
            {
                Scope = scope,
                Quantity = Int(M(def, "Quantity")),
                Weight = Int(M(def, "Weight")),
                Reward = RewardOf(M(def, "Reward")),
            };

            foreach (var cost in Items(M(def, "ItemCosts")))
                offer.Costs.Add(new ShopDump.CostEntry
                {
                    Currency = CurrencyOf(cost),
                    Amount = Long(M(cost, "CurrencyAmount")),
                });

            foreach (var seg in Items(M(def, "Segments")))
                offer.Segments.Add(Key(seg));

            foreach (var req in Items(M(def, "PlayerRequirements")))
                if (req != null) offer.Requirements.Add(req.GetType().Name);

            if (dump.FlashSaleOffers.ContainsKey(id))
                dump.Warnings.Add($"⚠ flash sale offer {id} is defined in both the garage and the event library");
            dump.FlashSaleOffers[id] = offer;
        }
    }

    /// <summary>Groups keyed by (placement, slot) — how a shop's settings address them.</summary>
    private static Dictionary<(string Placement, string Slot), List<ShopDump.FlashSaleGroupEntry>> CollectGroups(
        SharedGameConfig config, ShopDump dump)
    {
        var res = new Dictionary<(string, string), List<ShopDump.FlashSaleGroupEntry>>();

        foreach (var library in new[] { (object?)config.GarageFlashSaleGroups, config.EventFlashSaleGroups })
            foreach (var (id, g) in Entries(library))
            {
                var entry = new ShopDump.FlashSaleGroupEntry
                {
                    Id = id,
                    Weight = Int(M(g, "Weight")),
                    IgnoreRoll = M(g, "IgnoreRoll") is true,
                };
                foreach (var r in Items(M(g, "OfferRefs")))
                {
                    var offerId = Key(r);
                    if (offerId.Length == 0) continue;
                    entry.Offers.Add(offerId);
                    if (!dump.FlashSaleOffers.ContainsKey(offerId))
                        dump.Warnings.Add($"⚠ flash sale group {id} points at undefined offer {offerId}");
                }

                var key = (Str(M(g, "PlacementId")), Str(M(g, "SlotId")));
                if (!res.TryGetValue(key, out var list)) res[key] = list = new List<ShopDump.FlashSaleGroupEntry>();
                list.Add(entry);
            }

        return res;
    }

    private static void CollectShops(SharedGameConfig config,
        Dictionary<(string Placement, string Slot), List<ShopDump.FlashSaleGroupEntry>> groups, ShopDump dump)
    {
        foreach (var (id, settings) in Entries(config.FlashSaleShopSettings))
        {
            var placement = Str(M(settings, "PlacementId"));
            var shop = new ShopDump.FlashSaleShopEntry
            {
                Id = id,
                Placement = placement,
                RefreshCurrency = Str(M(settings, "FlashResetCurrency")),
                RefreshDisabled = M(settings, "RefreshDisabled") is true,
            };

            foreach (var c in Items(M(settings, "FlashResetCosts")))
                if (c is int cost) shop.RefreshCosts.Add(cost);

            foreach (var s in Items(M(settings, "ActiveFlashSaleSlots")))
            {
                var slot = Str(s);
                var entry = new ShopDump.FlashSaleSlotEntry { Slot = slot };
                if (groups.TryGetValue((placement, slot), out var bound)) entry.Groups.AddRange(bound);
                else dump.Warnings.Add($"⚠ flash sale shop {id} has slot {slot} with no group in placement {placement}");
                shop.Slots.Add(entry);
            }

            dump.FlashSaleShops.Add(shop);
        }
    }

    // ── shop items ────────────────────────────────────────────────────

    private static void CollectShopItems(SharedGameConfig config, ShopDump dump)
    {
        foreach (var (id, info) in Entries(config.ShopItems))
        {
            var actual = M(info, "ActualItem");
            var entry = new ShopDump.ShopItemEntry
            {
                Id = id,
                Category = Str(M(info, "ShopCategory")),
                Kind = actual?.GetType().Name ?? "",
                Item = ItemTypeOf(M(actual, "ItemDef"), config),
                Board = Str(M(actual, "BoardId")),
                Disabled = M(info, "Disabled") is true,
                UnderMore = M(info, "IsUnderMore") is true,
                ForWebShop = M(info, "ForWebShopShop") is true,
                Price = PriceOf(M(actual, "PriceCurve")),
                Limit = LimitOf(M(actual, "PurchaseLimiter")),
            };
            dump.ShopItems.Add(entry);
        }
    }

    /// <summary>
    /// A price curve flattened to its type, currency and numeric members. Fixed-point values are
    /// rounded up like the dump engines do (<c>MetaMathConverter</c> with ceiling): the config
    /// stores 50 coins as 49.99999999999999.
    /// </summary>
    private static ShopDump.PriceEntry? PriceOf(object? curve)
    {
        if (curve == null) return null;
        var entry = new ShopDump.PriceEntry { Type = curve.GetType().Name };
        foreach (var m in MetaObjectWriter.Members(curve))
        {
            object? v;
            try { v = m.Get(); } catch { continue; }
            switch (v)
            {
                case null: break;
                case F64 f64: entry.Values[m.Name] = Ceil(f64.Double); break;
                case F32 f32: entry.Values[m.Name] = Ceil(f32.Double); break;
                case int i: entry.Values[m.Name] = i; break;
                default:
                    // The curve's Currency enum, or the limiter it nests for its own step counting;
                    // the nested limiter is already reported on the item itself.
                    if (v.GetType().IsEnum && entry.Currency.Length == 0) entry.Currency = v.ToString() ?? "";
                    break;
            }
        }
        return entry;
    }

    private static ShopDump.LimitEntry? LimitOf(object? limiter)
    {
        if (limiter == null) return null;
        var entry = new ShopDump.LimitEntry { Type = limiter.GetType().Name };
        foreach (var m in MetaObjectWriter.Members(limiter))
        {
            object? v;
            try { v = m.Get(); } catch { continue; }
            if (v is int i) entry.Values[m.Name] = i;
            else if (v != null && v.GetType().IsEnum) entry.Values[m.Name] = Convert.ToInt32(v);
        }
        return entry;
    }

    // ── shared helpers ────────────────────────────────────────────────

    /// <summary>
    /// A reward flattened to what a shop row needs: what it is, how many, and which board it lands
    /// on. Walking the members rather than switching on the reward type keeps every reward kind
    /// (item, energy, currency) readable with one rule.
    /// </summary>
    private static ShopDump.RewardEntry? RewardOf(object? reward)
    {
        if (reward == null) return null;
        var name = reward.GetType().Name;
        var entry = new ShopDump.RewardEntry
        {
            Kind = name.StartsWith("Reward", StringComparison.Ordinal) ? name["Reward".Length..] : name,
        };

        foreach (var m in MetaObjectWriter.Members(reward))
        {
            object? v;
            try { v = m.Get(); } catch { continue; }
            if (v == null) continue;
            switch (m.Name)
            {
                case "ItemDef": entry.Id = ItemTypeOf(v, ClientGlobal.SharedGameConfig); break;
                case "Amount": entry.Amount = Int(v); break;
                case "MergeBoardId": entry.Board = Str(v); break;
                case "EnergyType" or "CurrencyType" or "RewardId" when entry.Id.Length == 0: entry.Id = Str(v); break;
            }
        }
        return entry;
    }

    /// <summary>Currency of an <c>ICost</c>: the game currency enum, an event currency id, or None.</summary>
    private static string CurrencyOf(object? cost)
    {
        var type = M(cost, "Type");
        if (type != null) return Str(type);
        var eventCurrency = M(cost, "EventCurrencyId");
        return eventCurrency != null ? Str(eventCurrency) : "None";
    }

    private static string ItemTypeOf(object? itemDef, SharedGameConfig? config)
    {
        if (itemDef is not ItemDef def || config?.Items == null) return "";
        return config.Items.TryGetValue(def.ConfigKey, out var d) && d?.ItemType != null ? d.ItemType : $"#{def.ConfigKey}";
    }

    /// <summary>Key/value pairs of a <c>GameConfigLibrary</c>, addressed without its generic types.</summary>
    private static IEnumerable<(string Id, object Value)> Entries(object? library)
    {
        if (library == null) yield break;
        var enumerate = library.GetType().GetMethod("EnumerateAll");
        if (enumerate?.Invoke(library, null) is not System.Collections.IEnumerable pairs) yield break;

        foreach (var kv in pairs)
        {
            var t = kv.GetType();
            var id = t.GetProperty("Key")?.GetValue(kv)?.ToString() ?? "";
            var value = t.GetProperty("Value")?.GetValue(kv);
            if (id.Length > 0 && value != null) yield return (id, value);
        }
    }

    private static IEnumerable<object?> Items(object? list)
        => list as System.Collections.IEnumerable is { } items and not string ? items.Cast<object?>() : Array.Empty<object?>();

    /// <summary>Null-safe <c>[MetaMember]</c> read (the member's parent is often optional).</summary>
    private static object? M(object? obj, string name) =>
        obj == null ? null : MetaObjectWriter.GetMember(obj, name, ConsoleDumpLog.Instance);

    /// <summary>ToString, except a MetaRef stringifies as its type name — print the key instead.</summary>
    private static string Str(object? v) => v is IMetaRef r ? r.KeyObject?.ToString() ?? "" : v?.ToString() ?? "";

    private static string Key(object? v) => Str(v is IMetaRef r ? r.KeyObject : M(v, "ConfigKey") ?? v);

    private static int Int(object? v) => v is int i ? i : (v != null && int.TryParse(Str(v), out var p) ? p : 0);

    private static long Long(object? v) => v is long l ? l : (v != null && long.TryParse(Str(v), out var p) ? p : 0);

    private static double Ceil(double d) => Math.Ceiling(d) == 0d ? 0d : Math.Ceiling(d);
}
