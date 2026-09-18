namespace MergeMansionWikiTools.Models;

/// <summary>
/// Contents of <c>shop_items.json</c> — the shop tables priced in Coins/Gems, as written by
/// <c>ShopExtractor</c>. Two independent mechanisms live here, because the game's Shop popup shows
/// them side by side and neither is in any dump file:
/// <list type="bullet">
/// <item><description><b>Flash sales</b> (<see cref="FlashSaleShops"/> + <see cref="FlashSaleOffers"/>)
/// — the rotating slots with a stock counter ("10 Left") and a per-purchase price ladder. Every
/// event shop is one of these, keyed by the event's <c>BoardShopPlacementIds</c> entry.</description></item>
/// <item><description><b>Shop items</b> (<see cref="ShopItems"/>) — the permanent garage shop rows
/// (daily deals, chests, energy and gem packs), priced by a curve rather than a ladder.</description></item>
/// </list>
/// <para>
/// Why it is not in events.json: neither table is a dump engine's output, and teaching an engine to
/// emit them would break the byte-for-byte Legacy/Native parity the engines are verified with. The
/// app therefore reads them straight off the imported config, exactly as it does for
/// <c>daily_scoop.json</c> (see <see cref="DailyScoopDump"/>).
/// </para>
/// </summary>
public sealed class ShopDump
{
    /// <summary>Flash sale shops: the garage one plus one per event, newest config order.</summary>
    public List<FlashSaleShopEntry> FlashSaleShops { get; set; } = new();

    /// <summary>Every flash sale candidate by config id, referenced from the slots' groups.</summary>
    public Dictionary<string, FlashSaleOfferEntry> FlashSaleOffers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The <c>ShopItems</c> library — permanent shop rows (garage deals, chests, packs).</summary>
    public List<ShopItemEntry> ShopItems { get; set; } = new();

    /// <summary>Anything the extractor could not resolve; surfaced in the Update dialog's notes.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// One flash sale shop: <c>FlashSaleShopSettings</c> plus the slots resolved against the group
    /// libraries, in the order the game lays them out.
    /// </summary>
    public sealed class FlashSaleShopEntry
    {
        /// <summary>Settings id; for an event shop this is its <c>BoardShopPlacementIds</c> entry
        /// (<c>LDEMurderAtTheMansionShop</c>).</summary>
        public string Id { get; set; } = "";

        /// <summary>Offer placement the slots belong to (<c>LDEMurderAtTheMansionShopFlash</c>).</summary>
        public string Placement { get; set; } = "";

        /// <summary>Currency of the manual refresh button (<c>Diamonds</c> everywhere so far).</summary>
        public string RefreshCurrency { get; set; } = "";

        /// <summary>
        /// Refresh price ladder. A single value means a flat price; several mean the first refreshes
        /// of a cycle are cheaper (<c>[0, 5, 10, 15, 20]</c>).
        /// </summary>
        public List<int> RefreshCosts { get; set; } = new();

        /// <summary>True when the shop has no refresh button at all.</summary>
        public bool RefreshDisabled { get; set; }

        /// <summary>Active slots, in display order.</summary>
        public List<FlashSaleSlotEntry> Slots { get; set; } = new();
    }

    /// <summary>
    /// One slot of a shop. Event shops use fixed slots with a single candidate (what is on offer is
    /// known in advance); the garage shop rolls a weighted group per slot, so several candidates can
    /// appear in the same position.
    /// </summary>
    public sealed class FlashSaleSlotEntry
    {
        public string Slot { get; set; } = "";

        /// <summary>Groups bound to this slot, each a weighted set of candidates.</summary>
        public List<FlashSaleGroupEntry> Groups { get; set; } = new();
    }

    public sealed class FlashSaleGroupEntry
    {
        public string Id { get; set; } = "";

        /// <summary>Roll weight against the other groups of the same slot.</summary>
        public int Weight { get; set; }

        /// <summary>True when the group bypasses the roll and is always placed.</summary>
        public bool IgnoreRoll { get; set; }

        /// <summary>Candidate offer ids; keys into <see cref="FlashSaleOffers"/>.</summary>
        public List<string> Offers { get; set; } = new();
    }

    public sealed class FlashSaleOfferEntry
    {
        /// <summary><c>"Event"</c> or <c>"Garage"</c> — which of the two libraries defines it.</summary>
        public string Scope { get; set; } = "";

        /// <summary>Stock of the slot; the game shows it as "N Left".</summary>
        public int Quantity { get; set; }

        /// <summary>Roll weight inside its group.</summary>
        public int Weight { get; set; }

        /// <summary>
        /// Price per purchase, ascending: the first entry is what an untouched slot costs, the
        /// second what the next copy costs, and so on. A ladder shorter than <see cref="Quantity"/>
        /// keeps its last price for the remaining stock.
        /// </summary>
        public List<CostEntry> Costs { get; set; } = new();

        /// <summary>What the purchase hands over.</summary>
        public RewardEntry? Reward { get; set; }

        /// <summary>Player segments the offer is restricted to; empty means everyone.</summary>
        public List<string> Segments { get; set; } = new();

        /// <summary>Availability requirement type names, when the offer carries any.</summary>
        public List<string> Requirements { get; set; } = new();
    }

    public sealed class CostEntry
    {
        /// <summary><c>Coins</c>, <c>Diamonds</c>, an event currency id, or <c>None</c>.</summary>
        public string Currency { get; set; } = "";

        public long Amount { get; set; }
    }

    /// <summary>One permanent shop row (<c>ShopItemInfo</c>) with its polymorphic parts flattened.</summary>
    public sealed class ShopItemEntry
    {
        public string Id { get; set; } = "";

        /// <summary>Tab/category the row is filed under (<c>Daily</c>, <c>Boxes</c>, <c>Chests</c>, …).</summary>
        public string Category { get; set; } = "";

        /// <summary>Concrete <c>IShopItem</c> kind: <c>BoardShopItem</c>, <c>EnergyItem</c>,
        /// <c>DiamondItem</c>, <c>RealMoneyItem</c>, …</summary>
        public string Kind { get; set; } = "";

        /// <summary>ItemType of the sold item, for the kinds that sell one.</summary>
        public string Item { get; set; } = "";

        /// <summary>Board the row belongs to (<c>Garage</c>, an event board, …).</summary>
        public string Board { get; set; } = "";

        public PriceEntry? Price { get; set; }

        public LimitEntry? Limit { get; set; }

        public bool Disabled { get; set; }

        /// <summary>True when the row lives behind the shop's "More" section.</summary>
        public bool UnderMore { get; set; }

        /// <summary>True when the row is also sold in the web shop.</summary>
        public bool ForWebShop { get; set; }
    }

    /// <summary>
    /// A price curve. <see cref="Type"/> names the curve and <see cref="Values"/> carries its own
    /// parameters, because each curve shape has different ones: <c>Price</c> for a constant one,
    /// <c>BasePrice</c>/<c>Increment</c> for a linear one (plus <c>MaxPrice</c> when saturated),
    /// <c>Base</c>/<c>Quotient</c> for a power one.
    /// </summary>
    public sealed class PriceEntry
    {
        public string Type { get; set; } = "";

        public string Currency { get; set; } = "";

        public Dictionary<string, double> Values { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A purchase limiter, flattened the same way: <c>MaxPurchases</c> for a lifetime cap,
    /// <c>PurchasesPerDay</c> for a daily one, and so on. A limiter without parameters
    /// (<c>NoLimitPurchaseLimiter</c>) has an empty <see cref="Values"/>.
    /// </summary>
    public sealed class LimitEntry
    {
        public string Type { get; set; } = "";

        public Dictionary<string, int> Values { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class RewardEntry
    {
        /// <summary>Reward class without the <c>Reward</c> prefix: <c>Item</c>, <c>Energy</c>, …</summary>
        public string Kind { get; set; } = "";

        /// <summary>ItemType for an item reward, energy/currency type otherwise.</summary>
        public string Id { get; set; } = "";

        public int Amount { get; set; } = 1;

        /// <summary>Merge board the reward lands on, when it is board-bound.</summary>
        public string Board { get; set; } = "";
    }
}
