using MergeMansionWikiTools.Models;



namespace MergeMansionWikiTools.Services;



/// <summary>

/// Applies <c>Module:Datatable/Items/Mapping</c> to the loaded chains: renames items, overrides

/// levels, flags aliases/variants, regroups items into the chains the WIKI shows (several game

/// chains merge into one page) and marks real level collisions.

/// <para>

/// Lived in <c>MainWindow</c> until v0.24.78. Moving it out is what makes the merged state

/// reproducible OUTSIDE the UI — generator output depends on it (an alias contributes its values to

/// the merged row), so neither a test nor a repro harness could see what the app really renders. The

/// only thing left behind in the window is the chain-browser refresh that follows.

/// </para>

/// </summary>

public static class ChainMergeService

{

    /// <summary>Mutates <paramref name="data"/>'s chains in place. No-op when the mapping is empty.</summary>

    public static void ApplyWikiMapping(DataService? data, WikiMappingCache? mapping)

    {

        if (data == null || mapping == null || mapping.Mappings.Count == 0) return;

        using var _t = AppLogger.Timed("ApplyWikiMappingToChains");



        var mappings = mapping.Mappings;



        // ── Phase 1: Apply field overrides per item ──

        // Record each item's wiki-specified chainName (if any)

        var itemWikiChainName = new Dictionary<ParsedItem, string>(); // item → wiki chainName

        var itemIsAlias = new HashSet<ParsedItem>(); // items marked as alias in wiki mapping



        foreach (var chain in data.Chains)

        {

            foreach (var item in chain.Items)

            {

                if (string.IsNullOrEmpty(item.ItemType)) continue;

                if (!mappings.TryGetValue(item.ItemType, out var entry)) continue;



                // Override item name

                if (!string.IsNullOrEmpty(entry.Name))

                {

                    item.Name = entry.Name;

                    data.ItemNames[item.ItemType] = entry.Name;

                }



                // Override level

                if (entry.Level.HasValue)

                    item.Level = entry.Level.Value;



                // Track alias flag

                if (entry.IsAlias)

                {

                    item.IsAlias = true;

                    itemIsAlias.Add(item);

                }



                // Track transient flag (pass-through stage — never linked to, folded through instead)
                if (entry.IsTransient)
                    item.IsTransient = true;

                // Track FTUE flag (first-playthrough-only copy — kept on the page, values skipped)
                if (entry.IsFtue)
                    item.IsFtue = true;

                // Track variant flag (explicit display variant — also defers collision like alias)

                if (entry.IsVariant)

                {

                    item.IsVariant = true;

                    item.MappingVariantLabel = entry.VariantLabel; // isVariant = "Spring" → "Spring"

                    item.MappingVariantItemType = entry.VariantItemType;

                    item.MappingVariantOrder = entry.VariantOrder;

                    item.MappingGroupOdds = entry.GroupOdds;

                }



                // Record wiki chainName for Phase 2

                if (!string.IsNullOrEmpty(entry.ChainName))

                    itemWikiChainName[item] = entry.ChainName;

            }

        }



        // ── Phase 2: Build flat item list with effective chainName ──

        // Each entry: (item, effectiveChainName, sourceChain)

        var flatItems = new List<(ParsedItem Item, string EffectiveChainName, ParsedChain SourceChain)>();



        // Source chains that were (at least partially) adopted onto the wiki — i.e. have ≥1 item with

        // an explicit wiki chainName. Used by Phase 3.5 to detect unmapped stragglers.

        var adoptedSourceKeys = new HashSet<string>(StringComparer.Ordinal);



        foreach (var chain in data.Chains)

        {

            bool chainAdopted = chain.Items.Any(i => itemWikiChainName.ContainsKey(i));

            if (chainAdopted) adoptedSourceKeys.Add(chain.ConfigKey);



            foreach (var item in chain.Items)

            {

                // The wiki mapping's chainName is the SOLE source of wiki chain membership: an item that

                // carries one joins that chain; an item without one keeps its original game chain name.

                // No "chain-level default" inference — that used to vacuum unmapped items (e.g. a newly

                // added top level) into a renamed wiki chain, silently hiding that they lack a mapping.

                string effectiveName = itemWikiChainName.TryGetValue(item, out var itemSpecific)

                    ? itemSpecific

                    : chain.DisplayName;



                item.SourceChainKey = chain.ConfigKey;

                flatItems.Add((item, effectiveName, chain));

            }

        }



        // ── Phase 3: Group items by effectiveChainName → build new chains ──

        var newChains = new List<ParsedChain>();



        foreach (var group in flatItems.GroupBy(x => x.EffectiveChainName, StringComparer.Ordinal))

        {

            var items = group.OrderBy(x => x.Item.Level).Select(x => x.Item).ToList();

            var sourceChains = group.Select(x => x.SourceChain).Distinct().ToList();

            var primarySource = sourceChains[0];



            // Collect all source ConfigKeys

            var allConfigKeys = sourceChains.Select(c => c.ConfigKey).Distinct().ToList();



            // Check if the effective name came from wiki mapping

            bool nameFromWiki = group.Any(x => itemWikiChainName.ContainsKey(x.Item));



            // Determine display name: customName (from any source) > effectiveChainName

            string? customName = null;

            foreach (var sc in sourceChains)

            {

                if (!string.IsNullOrEmpty(sc.CustomName))

                {

                    customName = sc.CustomName;

                    break;

                }

            }



            var newChain = new ParsedChain

            {

                ConfigKey = primarySource.ConfigKey,

                PoolTag = primarySource.PoolTag,

                HasTestTag = primarySource.HasTestTag,

                DisplayName = customName ?? group.Key,

                OriginalName = primarySource.OriginalName,

                HasNaturalName = primarySource.HasNaturalName,

                CustomName = customName,

                IsNameFromWiki = nameFromWiki && customName == null,

                MergedFromConfigKeys = allConfigKeys.Count > 1 ? allConfigKeys : null,

                Items = items,

            };



            newChains.Add(newChain);

        }



        // ── Phase 3.5: Detect unmapped stragglers ──

        // A "straggler" chain is a data-named remainder (IsNameFromWiki == false) that contains items

        // whose game source chain was partially adopted onto the wiki (some siblings carry a wiki

        // chainName and moved into a renamed chain). Because grouping is by name string, unmapped items

        // whose data name EQUALS the wiki name already re-joined the wiki chain (which is IsNameFromWiki

        // == true) — so those are never flagged. Only genuine splits (data name ≠ wiki name) surface.

        if (adoptedSourceKeys.Count > 0)

        {

            // Map each adopted source chain to the wiki name its mapped items ended up under.

            var wikiNameBySource = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var chain in newChains.Where(c => c.IsNameFromWiki))

                foreach (var item in chain.Items)

                    if (adoptedSourceKeys.Contains(item.SourceChainKey))

                        wikiNameBySource[item.SourceChainKey] = chain.DisplayName;



            foreach (var chain in newChains.Where(c => !c.IsNameFromWiki))

            {

                var parentKey = chain.Items

                    .Select(i => i.SourceChainKey)

                    .FirstOrDefault(k => wikiNameBySource.ContainsKey(k));

                if (parentKey != null)

                {

                    chain.IsUnmappedStraggler = true;

                    chain.StragglerParentWikiName = wikiNameBySource[parentKey];

                }

            }

        }



        // ── Phase 4: Replace data.Chains + update dictionaries ──

        data.Chains.Clear();

        data.Chains.AddRange(newChains);



        foreach (var chain in newChains)

        {

            data.ChainNames[chain.ConfigKey] = chain.DisplayName;

            if (chain.MergedFromConfigKeys != null)

            {

                foreach (var key in chain.MergedFromConfigKeys)

                    data.ChainNames[key] = chain.DisplayName;

            }

        }



        // ── Phase 5: Detect level collisions ──

        // Skip collision warnings when all items in a level group are aliases,

        // or when it's a mix of alias + non-alias items (alias defers to primary).

        foreach (var chain in newChains)

        {

            var levelGroups = chain.Items.GroupBy(i => i.Level).Where(g => g.Count() > 1).ToList();

            if (levelGroups.Count > 0)

            {

                bool anyRealCollision = false;

                foreach (var lg in levelGroups)

                {

                    // Only "primary" items can collide — aliases AND explicit variants defer

                    // (both are intentional same-level multiplicities, not real collisions).

                    // Transients join aliases and variants here: all three are deliberate
                    // same-level multiplicities, not a mapping mistake to flag.
                    var nonAliasItems = lg
                        .Where(item => !itemIsAlias.Contains(item) && !item.IsVariant && !item.IsTransient)
                        .ToList();

                    if (nonAliasItems.Count <= 1) continue;



                    anyRealCollision = true;

                    foreach (var item in nonAliasItems)

                        item.IsColliding = true;

                }

                chain.HasLevelCollisions = anyRealCollision;

            }

        }


    }

}

