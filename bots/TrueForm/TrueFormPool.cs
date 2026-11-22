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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's spells (Niduus, 2026-11-19: "This bot will have every spell in the game ... Relying more on Vanilla spells unless
/// specially requested", "is to never have duplicate spells, so pick every spell in the data base that is more inclined to be
/// used. With this, he is also to get Treasure Cards as well", "include Spellement upgrades at max").
///
/// Every battle spell a wizard can get in the real game, once each: the trainers' spells, the spells quests teach, and the
/// treasure cards. A spell with several versions (trained and treasure card, or the tiers of a spell upgraded with
/// Spellements) is one "family" and counts once: the trained one before its treasure card, and an upgraded spell at its
/// highest tier (both paths read; the stronger top is kept). Left out: cantrips, pet spells, battleground and PvP only
/// spells, cards Ahziael marks broken, and forms like Polymorph.
///
/// Each family keeps the level a wizard can first have it (the trainer's level, the quest's, or the card's own level), so a
/// group's head of level 30 gets True Form's spells of level 30 and under. Dev spells (the Dev trainer tomes' list) come only
/// while he is grouped with a player, and never one that one-shots (Niduus: "just no dev spell one shots like Minotaur of
/// 999999999 damage"): an instant kill, a hit of a share of max health, or damage far over any real spell's.
/// </summary>
internal static class TrueFormPool {

    /// <summary>One spell he can be dealt.</summary>
    internal sealed record Entry(uint Id, string Family, string Shown, int Level, string School, bool Treasure, bool Dev, int Tier, Card Card);

    // Each family's versions, best first.
    private static volatile Dictionary<string, List<Entry>> s_families;
    private static volatile List<Entry> s_dev;
    private static volatile List<Entry> s_devOneShots = [];
    private static volatile HashSet<uint> s_devOneShotIds = [];
    private static volatile HashSet<uint> s_rejected;

    /// <summary>Spells that are no player's (monster, boss or out-of-line versions): never trained, owned or in his deck.</summary>
    internal static bool NotForPlayers(uint id) => s_rejected?.Contains(id) == true;
    private static int s_building;

    /// <summary>The highest base damage of any real spell, for the dev one-shot check.</summary>
    internal static int TopRealDamage { get; private set; }

    /// <summary>How many spell families he has (0 while still reading).</summary>
    internal static int Families => s_families?.Count ?? 0;

    /// <summary>How many dev spells he may use in a group (0 while still reading).</summary>
    internal static int DevCount => s_dev?.Count ?? 0;

    /// <summary>How many dev spells were left out as one-shots.</summary>
    internal static int DevOneShots { get; private set; }

    /// <summary>
    /// The dev spells that one-shot (left out of True Form's hands): Zoom, the quest scout, may use them (Niduus, 2026-11-22:
    /// "able to one shot all enemies as much as he sees fit"). Empty while still reading.
    /// </summary>
    internal static List<Entry> DevOneShotList => s_devOneShots;

    /// <summary>Whether this spell is one of the dev one-shots (<see cref="DevOneShotList"/>).</summary>
    internal static bool IsDevOneShot(uint id) => s_devOneShotIds.Contains(id);

    /// <summary>Whether the spells are read (the first ask starts reading them; it takes a little while).</summary>
    internal static bool Ready {
        get {
            if (s_families is not null) {
                return true;
            }

            if (Interlocked.Exchange(ref s_building, 1) == 0) {
                _ = Task.Run(() => {
                    // The list kept from last time first, so he is ready at once (reading every spell takes about five
                    // minutes, and after each bot update a school switch waited that long: Niduus, 2026-11-22, "he takes
                    // a long time"); then read again, which replaces it.
                    LoadSaved();
                    try {
                        Build();
                        Save();
                    }
                    catch (Exception e) {
                        Logger.Warning("True Form: his spells could not be read: {0}", Logger.Args(e.Message));
                        Interlocked.Exchange(ref s_building, 0);
                    }
                });
            }

            return false;
        }
    }

    /// <summary>
    /// The spells he has at this level, one per family (the best version of each he may have at that level), with the
    /// dev spells when <paramref name="dev"/>.
    /// </summary>
    internal static List<Entry> At(int level, bool dev) {
        if (!Ready) {
            return [];
        }

        var list = new List<Entry>(s_families.Count + (dev ? s_dev.Count : 0));
        foreach (var versions in s_families.Values) {
            if (versions.FirstOrDefault(v => v.Level <= level) is { } best) {
                list.Add(best);
            }
        }

        if (dev) {
            var have = new HashSet<string>(list.Select(e => e.Family), StringComparer.OrdinalIgnoreCase);
            list.AddRange(s_dev.Where(d => have.Add(d.Family)));
        }

        return list;
    }

    /// <summary>
    /// A spell of his by its name, as a player types it ("Dragonblade", "mass cleanse charm"), among the spells he has at that
    /// level: the exact name first, then a name that starts with it, then one that holds it. Null when none fits.
    /// </summary>
    internal static Entry Find(string name, int level, bool dev) {
        var words = (name ?? "").Trim().Trim('"', '\'', '.', '!', '?');
        if (words.Length < 3) {
            return null;
        }

        var all = At(level, dev);
        return all.FirstOrDefault(e => string.Equals(e.Shown, words, StringComparison.OrdinalIgnoreCase))
               ?? all.Where(e => e.Shown.StartsWith(words, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Dev).ThenBy(e => e.Shown.Length).FirstOrDefault()
               ?? all.Where(e => e.Shown.Contains(words, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Dev).ThenBy(e => e.Shown.Length).FirstOrDefault()
               // Spaces, dashes and apostrophes left out ("dragon blade", "sun serpent" for Sun-Serpent).
               ?? all.Where(e => Squash(e.Shown).StartsWith(Squash(words), StringComparison.Ordinal)).OrderBy(e => e.Dev).ThenBy(e => e.Shown.Length).FirstOrDefault();
    }

    private static string Squash(string text) => new((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Every version of every spell he may have at this level (not only each family's head), with the dev spells when
    /// <paramref name="dev"/>: for picking the strongest of a kind (enchantments: Epic over Monstrous) whatever family the
    /// game's names put it in.
    /// </summary>
    internal static List<Entry> Versions(int level, bool dev) {
        if (!Ready) {
            return [];
        }

        var list = s_families.Values.SelectMany(v => v).Where(v => v.Level <= level).ToList();
        if (dev) {
            list.AddRange(s_dev);
        }

        return list;
    }

    /// <summary>A spell of his by template, or null.</summary>
    internal static Entry Of(uint id) {
        if (!Ready) {
            return null;
        }

        return s_byId.TryGetValue(id, out var entry) ? entry : null;
    }

    private static Dictionary<uint, Entry> s_byId = [];

    // Never his (as the Bot Players' decks): forms and fused cards, and the derby's.
    private static readonly string[] s_neverNames = ["FUSE", "Mutate", "Polymorph", "Ramp_", "Mega "];

    private static readonly HashSet<string> s_schools = new(StringComparer.OrdinalIgnoreCase) {
        "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Sun", "Moon", "Star", "Shadow",
    };

    // One-shot effects a dev card may carry: an instant kill, or a hit of a share of max health.
    private static readonly HashSet<kSpellEffects> s_oneShotEffects = [kSpellEffects.kInstantKill, kSpellEffects.kKillCreature, kSpellEffects.kMaxHealthDamage];

    private const string SAVED_FILE = "spell-pool.json";

    private sealed record Saved(int TopRealDamage, int DevOneShots, List<uint> Rejected, List<SavedEntry> Entries);

    // Kind: "real" (in a family), "dev", "oneshot".
    private sealed record SavedEntry(uint Id, string Family, string Shown, int Level, string School, bool Treasure, int Tier, string Kind);

    private static string SavedPath => System.IO.Path.Combine(UltimusSeat.TrueForm.Folder, SAVED_FILE);

    private static void Save() {
        try {
            var entries = s_families.Values.SelectMany(v => v).Select(e => new SavedEntry(e.Id, e.Family, e.Shown, e.Level, e.School, e.Treasure, e.Tier, "real"))
                .Concat(s_dev.Select(e => new SavedEntry(e.Id, e.Family, e.Shown, e.Level, e.School, e.Treasure, e.Tier, "dev")))
                .Concat(s_devOneShots.Select(e => new SavedEntry(e.Id, e.Family, e.Shown, e.Level, e.School, e.Treasure, e.Tier, "oneshot")))
                .ToList();
            System.IO.Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            System.IO.File.WriteAllText(SavedPath, System.Text.Json.JsonSerializer.Serialize(new Saved(TopRealDamage, DevOneShots, [.. s_rejected], entries)));
        }
        catch (Exception e) {
            Logger.Debug("True Form: could not keep his spell list: {0}", Logger.Args(e.Message));
        }
    }

    // The list kept from last time, each spell's card read again from the game files (fast, one by one).
    private static void LoadSaved() {
        try {
            if (!System.IO.File.Exists(SavedPath)) {
                return;
            }

            var started = DateTime.UtcNow;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(SavedPath));
            if (saved?.Entries is not { Count: > 0 }) {
                return;
            }

            var real = new List<Entry>();
            var dev = new List<Entry>();
            var oneShots = new List<Entry>();
            foreach (var e in saved.Entries) {
                if (RewrittenAI.ReadTemplate(e.Id) is not { } card) {
                    continue;
                }

                var entry = new Entry(e.Id, e.Family, e.Shown, e.Level, e.School, e.Treasure, e.Kind != "real", e.Tier, card);
                (e.Kind == "real" ? real : e.Kind == "dev" ? dev : oneShots).Add(entry);
            }

            if (real.Count == 0 || s_families is not null) {
                return;
            }

            var byId = new Dictionary<uint, Entry>();
            foreach (var entry in real.Concat(dev).Concat(oneShots)) {
                byId.TryAdd(entry.Id, entry);
            }

            s_byId = byId;
            s_rejected = [.. saved.Rejected ?? []];
            s_dev = dev;
            s_devOneShots = oneShots;
            s_devOneShotIds = [.. oneShots.Select(e => e.Id)];
            DevOneShots = saved.DevOneShots;
            TopRealDamage = Math.Max(1, saved.TopRealDamage);
            // Saved best first in each family, so the order is kept.
            s_families = real.GroupBy(e => e.Family, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            Logger.Information("True Form: {0} spells from his kept list, in {1:0.0} s (read again in the background).",
                Logger.Args(s_families.Count, (DateTime.UtcNow - started).TotalSeconds));
        }
        catch (Exception e) {
            Logger.Debug("True Form: his kept spell list could not be read: {0}", Logger.Args(e.Message));
        }
    }

    private static void Build() {
        var started = DateTime.UtcNow;

        // Where each spell is first had: a trainer (its level), a quest (the quest's level).
        var trained = new Dictionary<uint, int>();
        foreach (var spell in VanillaTrainers.All.SelectMany(t => t.Spells).Where(s => s.Kind == "School")) {
            trained[spell.Id] = trained.TryGetValue(spell.Id, out var known) ? Math.Min(known, spell.Rank) : spell.Rank;
        }

        var quested = new Dictionary<uint, int>();
        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest is null) {
                continue;
            }

            var lists = new List<ResultList> { quest.m_startResults, quest.m_endResults };
            lists.AddRange((quest.m_goals ?? []).Where(g => g is not null).SelectMany(g => new[] { g.m_activateResults, g.m_completeResults }));
            foreach (var learn in lists.Where(l => l?.m_results is not null).SelectMany(l => l.m_results).OfType<ResLearnSpell>()) {
                if (learn.m_templateID == 0) {
                    continue;
                }

                var level = Math.Max(0, (int) quest.m_questLevel);
                quested[learn.m_templateID] = quested.TryGetValue(learn.m_templateID, out var known) ? Math.Min(known, level) : level;
            }
        }

        var devIds = new HashSet<uint>(HouseTrainerSets.DevSpells().Select(d => (uint) d.Id));

        // The tier groups: each upgradable spell's depth (tier one = 1) along its paths.
        var tierDepth = new Dictionary<uint, (int Group, int Depth)>();
        try {
            var groups = TieredSpells.Groups;
            for (var g = 0; g < groups.Count; g++) {
                string tierOne = groups[g]?.m_tierOneSpell;
                if (string.IsNullOrEmpty(tierOne)) {
                    continue;
                }

                var queue = new Queue<(string Name, int Depth)>([(tierOne, 1)]);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (queue.Count > 0) {
                    var (name, depth) = queue.Dequeue();
                    var id = TieredSpells.TemplateIdOf(name);
                    if (!seen.Add(name) || id == 0 || CoreObjectFactory.GetCoreTemplate(id) is not TieredSpellTemplate template || template.m_retired) {
                        continue;
                    }

                    tierDepth[id] = (g, depth);
                    foreach (var next in TieredSpells.NextTiers(template)) {
                        queue.Enqueue((next.Name, depth + 1));
                    }
                }
            }
        }
        catch (Exception e) {
            Logger.Warning("True Form: the spell upgrades could not be read: {0}", Logger.Args(e.Message));
        }

        var real = new List<Entry>();
        var dev = new List<Entry>();
        var rejected = new HashSet<uint>();
        foreach (var (path, template) in SpellFactory.AllSpellTemplates()) {
            if (template is null || template is CantripsSpellTemplate || template.m_spellSourceType == kSpellSourceType.kPet
                || template.m_battlegroundsOnly || template.m_PvP || template.m_spellRank is null) {
                continue;
            }

            string name = template.m_name;
            string school = template.m_sMagicSchoolName;
            if (string.IsNullOrEmpty(name) || !s_schools.Contains(school ?? "") || s_neverNames.Any(n => name.Contains(n, StringComparison.Ordinal))) {
                continue;
            }

            var id = SpellFactory.TemplateIdOf(template);
            if (id == 0 || !SpellFactory.CanGoInDeck(id) || BrokenCards.Skip(id)) {
                continue;
            }

            var shown = Shown(template);
            if (shown.Length == 0) {
                continue;
            }

            var card = RewrittenAI.ReadTemplate(id);
            if (card is null || card.Natural) {
                continue;
            }

            var isDev = devIds.Contains(id);
            var tc = template.m_Treasure;
            var tier = tierDepth.TryGetValue(id, out var t) ? t : (Group: -1, Depth: 0);
            var level = trained.TryGetValue(id, out var trainLevel) ? trainLevel
                : quested.TryGetValue(id, out var questLevel) && questLevel > 0 ? questLevel
                : template.m_levelRestriction > 0 ? template.m_levelRestriction
                : -1;
            // A real spell: one the trainers or quests teach, a treasure card, or a tier of an upgradable spell. A level
            // restriction alone is no sign: monster and boss versions carry one too (Niduus, 2026-11-19: a Scald of
            // 100000 Fire damage over 100 rounds was in his deck, "Cards like this are a nono").
            if (!isDev && !tc && tier.Group < 0 && !trained.ContainsKey(id) && !quested.ContainsKey(id)) {
                rejected.Add(id);
                continue;
            }

            if (level < 0) {
                // No level of its own: about where a spell of its pip cost comes (a 1 pip spell at level 1, 7 pips about 48).
                level = Math.Max(1, (RankOf(template) - 1) * 8);
            }

            var family = tier.Group >= 0 ? "tier:" + tier.Group : shown.ToLowerInvariant();
            var entry = new Entry(id, family, shown, level, school, tc, isDev, tier.Depth, card);
            (isDev ? dev : real).Add(entry);
        }

        // A spell that is also an upgradable spell's tier (the trained Fire Cat and Fire Cat's tier one) is that family.
        var tierByShown = real.Where(e => e.Tier > 0).GroupBy(e => e.Shown.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Family);
        real = [.. real.Select(e => e.Tier == 0 && tierByShown.TryGetValue(e.Family, out var tierFamily) ? e with { Family = tierFamily } : e)];

        // An upgraded tier is had from its tier one's level.
        var tierOneLevel = real.Where(e => e.Tier == 1).GroupBy(e => e.Family).ToDictionary(g => g.Key, g => g.Min(e => e.Level));
        real = [.. real.Select(e => e.Tier > 1 && tierOneLevel.TryGetValue(e.Family, out var first) ? e with { Level = first } : e)];

        // No monster or boss version passes as a player's: the strongest hit a trainer, quest or upgrade gives sets the
        // cap, and a version (a treasure card too) hitting far over the taught version of the same spell is left out.
        var taught = real.Where(e => !e.Treasure && (trained.ContainsKey(e.Id) || quested.ContainsKey(e.Id) || e.Tier > 0)).ToList();
        var taughtTop = Math.Max(1, taught.Where(e => e.Card.Hit).Select(e => Math.Max(e.Card.BaseDamage, e.Card.Tiers.Length > 0 ? e.Card.Tiers.Max(x => x.Damage) : 0))
            .DefaultIfEmpty(1).Max());
        var taughtByFamily = taught.GroupBy(e => e.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(e => Strength(e.Card)), StringComparer.OrdinalIgnoreCase);
        bool Outlier(Entry e) {
            var hit = Math.Max(e.Card.BaseDamage, e.Card.Tiers.Length > 0 ? e.Card.Tiers.Max(x => x.Damage) : 0);
            return hit > taughtTop * 3 / 2 || e.Card.PerPip * 4 > taughtTop * 3 / 2
                   || (taughtByFamily.TryGetValue(e.Family, out var own) && own > 0 && Strength(e.Card) > own * 2);
        }

        foreach (var e in real.Where(Outlier)) {
            rejected.Add(e.Id);
        }

        real = [.. real.Where(e => !Outlier(e))];

        var families = real.GroupBy(e => e.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Treasure ? 1 : 0)
                .ThenByDescending(e => e.Tier)
                .ThenByDescending(e => Strength(e.Card))
                .ThenByDescending(e => e.Level)
                .ThenBy(e => e.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        // The trained version first, but never one his level can't have while a treasure card of the same spell can: the
        // list is walked for the first version at or under the level asked, so a lower treasure card still comes up.
        TopRealDamage = Math.Max(1, real.Where(e => e.Card.Hit).Select(e => Math.Max(e.Card.BaseDamage, e.Card.Tiers.Length > 0 ? e.Card.Tiers.Max(x => x.Damage) : 0))
            .DefaultIfEmpty(1).Max());
        var cap = TopRealDamage * 3 / 2;
        var oneShots = 0;
        var devKept = new List<Entry>();
        var oneShotKept = new List<Entry>();
        foreach (var entry in dev.GroupBy(e => e.Family, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(e => Strength(e.Card)).First())) {
            if (OneShot(entry, cap)) {
                oneShots++;
                oneShotKept.Add(entry with { Level = 0, Family = "oneshot:" + entry.Family });
                continue;
            }

            devKept.Add(entry with { Level = 0, Family = "dev:" + entry.Family });
        }

        var byId = new Dictionary<uint, Entry>();
        foreach (var entry in families.Values.SelectMany(v => v).Concat(devKept).Concat(oneShotKept)) {
            byId.TryAdd(entry.Id, entry);
        }

        s_byId = byId;
        s_rejected = rejected;
        s_dev = devKept;
        s_devOneShots = oneShotKept;
        s_devOneShotIds = [.. oneShotKept.Select(e => e.Id)];
        DevOneShots = oneShots;
        s_families = families;
        Logger.Information("True Form: {0} spells, one of each ({1} versions read), {2} dev spells for groups ({3} dev one-shots left out), in {4:0.0} s.",
            Logger.Args(families.Count, real.Count, devKept.Count, oneShots, (DateTime.UtcNow - started).TotalSeconds));
    }

    private static int RankOf(SpellTemplate template) {
        try {
            return Math.Max(1, (int) template.m_spellRank.m_spellRank);
        }
        catch (Exception) {
            return 1;
        }
    }

    // How strong a version is, to pick the best of a family: its damage, its heal, its charm.
    private static double Strength(Card card)
        => card is null ? 0
            : card.BaseDamage + (card.Tiers.Length > 0 ? card.Tiers.Max(t => t.Damage) : 0) + card.HealAmount + (card.BladePercent * 10) + (card.TrapPercent * 10);

    /// <summary>A dev card that one-shots: an instant kill, a hit of a share of max health, or damage over the cap.</summary>
    private static bool OneShot(Entry entry, int cap) {
        var card = entry.Card;
        if (card.BaseDamage > cap || card.Tiers.Any(t => t.Damage > cap) || card.PerPip * 4 > cap) {
            return true;
        }

        try {
            return CoreObjectFactory.GetCoreTemplate(entry.Id) is SpellTemplate template
                   && CombatEnchantments.Leaves(template.m_effects ?? []).Any(e => e is not null
                       && (s_oneShotEffects.Contains(e.m_effectType) || (CombatCharms.DamageEffects.Contains(e.m_effectType) && e.m_effectParam > cap)));
        }
        catch (Exception) {
            return true;
        }
    }

    private static string Shown(SpellTemplate template) {
        try {
            string key = template.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key) ?? "";
            return shown.Trim();
        }
        catch (Exception) {
            return "";
        }
    }

}
