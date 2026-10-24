/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SPELL UPGRADES (TIERED SPELLS)
 * ========================================================================
 *
 * PURPOSE:
 * Spell upgrades as the game has them: a tiered spell (Fire Cat, Daybreaker, ...) is upgraded tier by tier with
 * that spell's own Spellement (a ReagentShard reagent), and from tier 2 the wizard picks a path (A, B, C, D).
 *
 * USAGE EXAMPLE:
 * var visibility = TieredSpells.Visibility(wizard);
 * var result = TieredSpells.TryUpgrade(playerActor, wizard, fromId, toId, out var message);
 *
 * NOTE:
 * - Root.wad's TieredSpells.xml (TieredSpellConfig) lists the 283 spell groups in the order the game counts them;
 *   each names its tier one spell and its Spellement (m_shardType). TieredSpellsGroupInfo.xml says which group
 *   every upgraded spell is in. The spells themselves are TieredSpellTemplate: m_nextTierSpells are the choices,
 *   m_newShardCost (or the older m_shardCost) is what the upgrade into that spell costs.
 * - The game asks with MSG_CANSEETIEREDSPELLGROUPS and reads Visibility as a binary TieredSpellGroupVisibility
 *   (ClientTieredSpellManager::MSG_CanSeeTieredSpellGroups, one entry per group, 1 = shown). Read from the game
 *   program, 2026-10-24.
 * - An upgrade replaces the spell in the spellbook and every copy of it in the wizard's decks.
 *
 * TODO:
 * - The Spellements 2.0 "multiple paths" messages (prep, reset, refund all) are not answered.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Spell upgrades: the game's tiered spells, upgraded with Spellements.
/// </summary>
internal static class TieredSpells {

    private const string CONFIG_FILE = "TieredSpells.xml";
    private const string GROUP_INFO_FILE = "TieredSpellsGroupInfo.xml";
    private const string RETIRED_FILE = "TieredSpellsRetired.xml";

    // The game reads the visibility with this property mask (Save | Public).
    private const PropertyFlags VISIBILITY_MASK = PropertyFlags.Prop_Save | PropertyFlags.Prop_Public;

    // [SpellUpgrades]
    private static readonly bool s_enabled = ConfigurationManager.GetValue("SpellUpgrades.Enabled", true);
    private static readonly bool s_oldCosts = ConfigurationManager.GetValue("SpellUpgrades.UseOldCosts", false);
    private static readonly int s_costPercent = ConfigurationManager.GetValue("SpellUpgrades.CostPercent", 100);
    private static readonly bool s_showAll = ConfigurationManager.GetValue("SpellUpgrades.ShowAllGroups", false);

    private static readonly object s_lock = new();
    private static bool s_loaded;
    private static List<TieredSpellGroup> s_groups = [];
    private static readonly Dictionary<string, int> s_groupOfSpell = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TieredSpellRetiredSpellData> s_retired = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, uint> s_templateIds = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Enabled => s_enabled;

    /// <summary>The spell groups in the game's order (the index is the game's group number).</summary>
    internal static IReadOnlyList<TieredSpellGroup> Groups {
        get {
            Load();

            return s_groups;
        }
    }

    private static void Load() {
        if (s_loaded) {
            return;
        }

        lock (s_lock) {
            if (s_loaded) {
                return;
            }

            try {
                var config = RootArchiveLoader.GetFile<TieredSpellConfig>(CONFIG_FILE);
                s_groups = config?.m_tieredSpellGroupList?.Where(g => g is not null).ToList() ?? [];
                if (config?.m_tieredSpellGroupList is not null && s_groups.Count != config.m_tieredSpellGroupList.Count) {
                    // An empty entry would shift every group number after it.
                    s_groups = [.. config.m_tieredSpellGroupList];
                }

                for (var i = 0; i < s_groups.Count; i++) {
                    string tierOne = s_groups[i]?.m_tierOneSpell;
                    if (!string.IsNullOrEmpty(tierOne)) {
                        s_groupOfSpell[tierOne] = i;
                    }
                }

                var info = RootArchiveLoader.GetFile<TieredSpellGroupInfoList>(GROUP_INFO_FILE);
                foreach (var entry in info?.m_tieredSpellGroupInfoList ?? []) {
                    string name = entry?.m_spellName;
                    var data = entry?.m_theTieredSpellGroupInfoData;
                    if (string.IsNullOrEmpty(name) || data is null || data.m_tsGroupIndex < 0 || data.m_tsGroupIndex >= s_groups.Count) {
                        continue;
                    }

                    s_groupOfSpell[name] = data.m_tsGroupIndex;
                }

                var retired = RootArchiveLoader.GetFile<TieredSpellRetired>(RETIRED_FILE);
                foreach (var entry in retired?.m_tieredSpellRetiredList ?? []) {
                    string name = entry?.m_spellName;
                    if (!string.IsNullOrEmpty(name)) {
                        s_retired[name] = entry;
                    }
                }

                Logger.Information("Spell upgrades | {0} spell groups, {1} upgradable spells, {2} retired.",
                    Logger.Args(s_groups.Count, s_groupOfSpell.Count, s_retired.Count));
            }
            catch (Exception ex) {
                Logger.Error("Spell upgrades | The game's spell upgrade files could not be read. {0}", Logger.Args(ex.Message));
            }

            s_loaded = true;
        }
    }

    /// <summary>The group number a spell is in, or -1.</summary>
    internal static int GroupOf(string spellName) {
        Load();

        return !string.IsNullOrEmpty(spellName) && s_groupOfSpell.TryGetValue(spellName, out var group) ? group : -1;
    }

    /// <summary>The spell template with this template ID or name hash (the game uses both), or null.</summary>
    internal static SpellTemplate ResolveTemplate(uint id, out uint templateId) {
        templateId = 0;
        if (id == 0) {
            return null;
        }

        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate byId) {
            templateId = id;

            return byId;
        }

        _ = SpellFactory.Exists("");
        var fromHash = SpellFactory.GetTemplateIdByHash(id);
        if (fromHash != 0 && CoreObjectFactory.GetCoreTemplate(fromHash) is SpellTemplate byHash) {
            templateId = fromHash;

            return byHash;
        }

        return null;
    }

    /// <summary>The template ID of the spell with this name, or 0.</summary>
    internal static uint TemplateIdOf(string spellName) {
        if (string.IsNullOrEmpty(spellName)) {
            return 0;
        }

        lock (s_templateIds) {
            if (s_templateIds.TryGetValue(spellName, out var cached)) {
                return cached;
            }
        }

        uint id = 0;
        if (SpellFactory.Exists(spellName)) {
            id = SpellFactory.GetTemplateIdByHash(StringHash.Compute(spellName));
        }

        lock (s_templateIds) {
            s_templateIds[spellName] = id;
        }

        return id;
    }

    /// <summary>What upgrading into this spell costs in Spellements.</summary>
    internal static int CostOf(TieredSpellTemplate target) {
        if (target is null) {
            return 0;
        }

        var cost = s_oldCosts || target.m_newShardCost <= 0 ? target.m_shardCost : target.m_newShardCost;

        return Math.Max(0, (int) Math.Ceiling(cost * Math.Max(0, s_costPercent) / 100.0));
    }

    /// <summary>The Spellement of a spell group (a reagent template ID), or 0.</summary>
    internal static ulong ShardOf(int group)
        => group >= 0 && group < Groups.Count && s_groups[group] is not null ? s_groups[group].m_shardType.Full : 0;

    /// <summary>How many of this Spellement the wizard has.</summary>
    internal static int ShardCount(Wizard wizard, ulong shardTemplateId) {
        if (shardTemplateId == 0) {
            return 0;
        }

        return wizard?.AlchemyBehavior?.Reagents?
            .Where(r => r is not null && (ulong) r.m_templateID == shardTemplateId)
            .Sum(r => Math.Max(0, r.m_quantity)) ?? 0;
    }

    /// <summary>The upgradable spells the wizard knows: template ID, template and group number.</summary>
    internal static List<(uint TemplateId, TieredSpellTemplate Template, int Group)> KnownTieredSpells(Wizard wizard) {
        var known = new List<(uint, TieredSpellTemplate, int)>();
        foreach (var templateId in wizard?.SpellbookBehavior?.LearnedSpellTemplateIds?.ToList() ?? []) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not TieredSpellTemplate template) {
                continue;
            }

            var group = GroupOf(template.m_name);
            if (group >= 0) {
                known.Add((templateId, template, group));
            }
        }

        return known;
    }

    /// <summary>The upgrades this spell can take next, with what each costs.</summary>
    internal static List<(string Name, uint TemplateId, TieredSpellTemplate Template, int Cost)> NextTiers(TieredSpellTemplate spell) {
        var next = new List<(string, uint, TieredSpellTemplate, int)>();
        foreach (var nameString in spell?.m_nextTierSpells ?? []) {
            string name = nameString;
            var id = TemplateIdOf(name);
            if (id == 0 || CoreObjectFactory.GetCoreTemplate(id) is not TieredSpellTemplate template || template.m_retired) {
                continue;
            }

            next.Add((name, id, template, CostOf(template)));
        }

        return next;
    }

    /// <summary>
    /// The game's TieredSpellGroupVisibility for this wizard: a group shows when the wizard knows one of its spells,
    /// and can upgrade when one of those has a next tier the wizard has the Spellements for.
    /// </summary>
    internal static TieredSpellGroupVisibility Visibility(Wizard wizard) {
        var groups = Groups;
        var visible = new byte[groups.Count];
        var canUpgrade = new int[groups.Count];
        if (s_showAll) {
            Array.Fill(visible, (byte) 1);
        }

        foreach (var (_, template, group) in KnownTieredSpells(wizard)) {
            visible[group] = 1;
            var shards = ShardCount(wizard, ShardOf(group));
            if (NextTiers(template).Any(n => n.Cost <= shards)) {
                canUpgrade[group] = 1;
            }
        }

        return new TieredSpellGroupVisibility {
            m_visibilityList = [.. visible],
            m_canUpgradeList = [.. canUpgrade],
            m_doPrepTieredSpellForMPList = [.. new byte[groups.Count]],
        };
    }

    /// <summary>The visibility answer for the game, or null when it cannot be made.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_CANSEETIEREDSPELLGROUPS VisibilityMessage(Wizard wizard) {
        try {
            var serializer = new ObjectSerializer(Versionable: false);
            if (!serializer.Serialize(Visibility(wizard), VISIBILITY_MASK, out var data)) {
                Logger.Error("Spell upgrades | The spell group list could not be serialized.");

                return null;
            }

            return new WIZARD2_53_PROTOCOL.MSG_CANSEETIEREDSPELLGROUPS {
                Visibility = data,
                CheckTieredSpellNotifications = 0,
                ShardType = 0,
            };
        }
        catch (Exception ex) {
            Logger.Error("Spell upgrades | The spell group list could not be made. {0}", Logger.Args(ex.Message));

            return null;
        }
    }

    /// <summary>
    /// Upgrades a spell the wizard knows into one of its next tiers, paying that tier's Spellements, and tells the
    /// game. A tier one spell that costs Spellements itself (Daybreaker - T01 - Base) can be bought with from 0.
    /// </summary>
    /// <returns>True when upgraded; <paramref name="message"/> says what happened in plain words.</returns>
    internal static bool TryUpgrade(IActorRef playerActor, Wizard wizard, uint fromId, uint toId, out string message) {
        message = "";
        if (!s_enabled) {
            message = "Spell upgrades are turned off on this server ([SpellUpgrades] Enabled).";

            return false;
        }

        if (wizard is null) {
            message = "No wizard.";

            return false;
        }

        if (ResolveTemplate(toId, out var toTemplateId) is not TieredSpellTemplate target) {
            message = $"The spell to upgrade into ({toId}) is not an upgradable spell.";

            return false;
        }

        string targetName = target.m_name;
        if (target.m_retired) {
            message = $"{targetName} is retired in the game and cannot be learned any more.";

            return false;
        }

        var group = GroupOf(targetName);
        if (group < 0) {
            message = $"{targetName} is in no spell group.";

            return false;
        }

        if (wizard.SpellbookBehavior.HasSpell(toTemplateId)) {
            message = $"You already know {targetName}.";

            return false;
        }

        // The spell being upgraded: the one the game named, else the known spell of the group that leads to it.
        uint fromTemplateId = 0;
        TieredSpellTemplate from = null;
        if (ResolveTemplate(fromId, out var namedId) is TieredSpellTemplate named && wizard.SpellbookBehavior.HasSpell(namedId)) {
            fromTemplateId = namedId;
            from = named;
        }

        if (from is null || !LeadsTo(from, targetName)) {
            var other = KnownTieredSpells(wizard).FirstOrDefault(k => k.Group == group && LeadsTo(k.Template, targetName));
            if (other.Template is not null) {
                fromTemplateId = other.TemplateId;
                from = other.Template;
            }
            else {
                from = null;
                fromTemplateId = 0;
            }
        }

        var cost = CostOf(target);
        var isTierOne = string.Equals((string) Groups[group].m_tierOneSpell, targetName, StringComparison.OrdinalIgnoreCase);
        if (from is null && !(isTierOne && cost > 0)) {
            message = $"You need the spell before {targetName} first.";

            return false;
        }

        var shard = ShardOf(group);
        var have = ShardCount(wizard, shard);
        if (have < cost) {
            message = $"{targetName} needs {cost} {ShardName(shard)} Spellements. You have {have}.";

            return false;
        }

        if (!TakeShards(playerActor, wizard, shard, cost)) {
            message = "The Spellements could not be taken.";

            return false;
        }

        var newSpell = SpellFactory.GetSpell(toTemplateId);
        if (newSpell is null || !wizard.LearnSpell(newSpell)) {
            // Give back what was paid.
            LootGranter.GrantReagent(playerActor, wizard, shard, cost);
            message = $"{targetName} could not be learned.";

            return false;
        }

        if (from is not null && wizard.UnlearnSpell(fromTemplateId)) {
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) fromTemplateId });
        }

        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) toTemplateId });

        var swapped = from is null ? 0 : SwapInDecks(playerActor, wizard, fromTemplateId, toTemplateId);

        message = from is null
            ? $"You learned {targetName} for {cost} Spellements."
            : $"{(string) from.m_name} became {targetName} for {cost} Spellements" + (swapped > 0 ? $"; {swapped} card(s) in your decks changed too." : ".");
        Logger.Information("Spell upgrades | {0}: {1}", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), message));

        return true;
    }

    /// <summary>
    /// Takes a retired upgrade back: the wizard gets the Spellements it cost (times the group's return rate) and
    /// the spell it came from (or the group's tier one spell).
    /// </summary>
    internal static bool TryRefund(IActorRef playerActor, Wizard wizard, uint spellId, uint oldSpellId, out string message) {
        message = "";
        if (!s_enabled || wizard is null) {
            message = "Spell upgrades are turned off.";

            return false;
        }

        Load();
        uint retiredId = 0;
        SpellTemplate retired = null;
        TieredSpellRetiredSpellData data = null;
        foreach (var id in new[] { spellId, oldSpellId }) {
            if (ResolveTemplate(id, out var templateId) is SpellTemplate template && wizard.SpellbookBehavior.HasSpell(templateId)
                && s_retired.TryGetValue((string) template.m_name, out var entry)) {
                retiredId = templateId;
                retired = template;
                data = entry;

                break;
            }
        }

        if (retired is null) {
            message = "You know no retired upgrade to refund.";

            return false;
        }

        string retiredName = retired.m_name;
        var group = GroupOf(retiredName);
        if (group < 0) {
            group = GroupOf(TierOneNameOf(retiredName));
        }

        if (group < 0) {
            message = $"{retiredName} is in no spell group.";

            return false;
        }

        // Back to the spell the game named, else to the tier one spell.
        var backId = 0u;
        foreach (var id in new[] { oldSpellId, spellId }) {
            if (ResolveTemplate(id, out var templateId) is SpellTemplate template && templateId != retiredId
                && !s_retired.ContainsKey((string) template.m_name)) {
                backId = templateId;

                break;
            }
        }

        if (backId == 0) {
            backId = TemplateIdOf(Groups[group].m_tierOneSpell);
        }

        var spent = s_oldCosts || data.m_totalShardsToUpgradeNew <= 0 ? data.m_totalShardsToUpgrade : data.m_totalShardsToUpgradeNew;
        var refund = (int) Math.Floor(Math.Max(0, spent) * (Groups[group].m_returnRate == 0 ? 100 : Groups[group].m_returnRate) / 100.0);

        if (wizard.UnlearnSpell(retiredId)) {
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) retiredId });
        }

        var back = backId == 0 ? null : SpellFactory.GetSpell(backId);
        if (back is not null && !wizard.SpellbookBehavior.HasSpell(backId) && wizard.LearnSpell(back)) {
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) backId });
        }

        if (backId != 0) {
            SwapInDecks(playerActor, wizard, retiredId, backId);
        }

        var shard = ShardOf(group);
        var given = refund > 0 ? LootGranter.GrantReagent(playerActor, wizard, shard, refund) : 0;
        message = $"{retiredName} was refunded: {given} Spellements back.";
        Logger.Information("Spell upgrades | {0}: {1}", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), message));

        return true;
    }

    // "Fire Cat - T03 - A" -> "Fire Cat".
    private static string TierOneNameOf(string name) {
        var cut = name.IndexOf(" - T", StringComparison.Ordinal);

        return cut > 0 ? name[..cut] : name;
    }

    private static bool LeadsTo(TieredSpellTemplate from, string targetName)
        => from?.m_nextTierSpells?.Any(n => string.Equals((string) n, targetName, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>The name the game shows for a Spellement (the reagent's display name), or its template ID.</summary>
    internal static string ShardName(ulong shardTemplateId) {
        var template = CoreObjectFactory.GetCoreTemplate(shardTemplateId) as WizItemTemplate;
        string display = template?.m_displayName;
        var shown = string.IsNullOrEmpty(display) ? null : Locale.GetEnglishName(display);

        return !string.IsNullOrEmpty(shown) ? shown : shardTemplateId.ToString();
    }

    private static bool TakeShards(IActorRef playerActor, Wizard wizard, ulong shardTemplateId, int count) {
        if (count <= 0) {
            return true;
        }

        var stacks = wizard.AlchemyBehavior?.Reagents?
            .Where(r => r is not null && (ulong) r.m_templateID == shardTemplateId && r.m_quantity > 0)
            .ToList() ?? [];
        var left = count;
        foreach (var stack in stacks) {
            if (left <= 0) {
                break;
            }

            var reagentId = stack.m_globalID.Full;
            var take = Math.Min(left, stack.m_quantity);

            // All but one straight off the stack, then the last through the bag so it saves once.
            stack.m_quantity -= take - 1;
            if (!wizard.RemoveReagent(reagentId, out var after)) {
                stack.m_quantity += take - 1;

                return false;
            }

            left -= take;
            if (after is not null && after.m_quantity > 0) {
                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = after.m_globalID, Quantity = after.m_quantity });
            }
            else {
                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = reagentId });
            }
        }

        return left <= 0;
    }

    /// <summary>Changes every copy of a spell in the wizard's decks into another spell. Returns how many changed.</summary>
    private static int SwapInDecks(IActorRef playerActor, Wizard wizard, uint fromTemplateId, uint toTemplateId) {
        var swapped = 0;
        try {
            var decks = (wizard.InventoryBehavior?.Items ?? []).Concat(wizard.EquipmentBehavior?.EquippedItems ?? [])
                .Where(item => item is not null)
                .GroupBy(item => item.m_globalID.Full)
                .Select(g => g.First())
                .ToList();
            foreach (var deck in decks) {
                if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deck, out var deckBehavior)) {
                    continue;
                }

                var copies = (int) (deckBehavior.m_spellList?.Where(s => s.m_templateID == fromTemplateId).Sum(s => (long) s.m_quantity) ?? 0);
                var deckId = deck.m_globalID.Full;
                for (var i = 0; i < copies; i++) {
                    if (!wizard.RemoveSpellFromDeck(fromTemplateId, deckId)) {
                        break;
                    }

                    playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK { SpellID = (int) fromTemplateId, DeckID = deckId, Success = 1 });
                    if (wizard.AddSpellToDeck(toTemplateId, deckId)) {
                        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK { SpellID = (int) toTemplateId, DeckID = deckId, Success = 1 });
                        swapped++;
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Spell upgrades | Changing the cards in the decks failed. {0}", Logger.Args(ex.Message));
        }

        return swapped;
    }

    /// <summary>Gives the wizard this many Spellements of every spell group it knows a spell of. Returns the total.</summary>
    internal static int GiveShardsForKnownSpells(IActorRef playerActor, Wizard wizard, int each) {
        var total = 0;
        foreach (var group in KnownTieredSpells(wizard).Select(k => k.Group).Distinct()) {
            var shard = ShardOf(group);
            if (shard != 0) {
                total += LootGranter.GrantReagent(playerActor, wizard, shard, each);
            }
        }

        return total;
    }

}
