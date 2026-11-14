/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// His personality grows from what really happened to him (Niduus, 2026-11-14: "create him a personality that adapts based
/// on his experience playing Wizard101"). Favorites and least favorites come from his own records, kept across restarts:
/// creatures from every fight (foes.json: who beat him, who he always beats, who beat him first and then lost), worlds and
/// areas from his world times and where time was lost (worlds.json, time-waste.json), schools from the schools of the
/// creatures that beat him or that he beat, quests from how long they took against what was expected, and players from who
/// talked and fought with him most. Each answer says why. Null when he has no experience of it yet (a stock answer then).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private string Opinion(string topic, bool least) {
        try {
            if (topic.StartsWith("like:", StringComparison.Ordinal)) {
                return LikeOpinion(topic[5..].Trim());
            }

            return topic switch {
                "creature" => CreatureOpinion(least),
                "school" => SchoolOpinion(least),
                "world" => WorldOpinion(least),
                "area" => AreaOpinion(least),
                "quest" => QuestOpinion(least),
                "player" => PlayerOpinion(least),
                _ => null,
            };
        }
        catch (Exception) {
            return null;
        }
    }

    private static string CreatureOpinion(bool least) {
        var foes = UltimusFoes.All().Where(f => f.Fought > 0 && !string.IsNullOrWhiteSpace(f.Name)).ToList();
        if (least) {
            var worst = foes.Where(f => f.Lost > 0).OrderByDescending(f => f.Lost).ThenByDescending(f => f.AverageHealthLost).FirstOrDefault()
                        ?? foes.OrderByDescending(f => f.AverageHealthLost).FirstOrDefault(f => f.AverageHealthLost > 0.3);
            return worst is null ? null
                : worst.Lost > 0 ? $"{worst.Name}. It beat me {worst.Lost} time{(worst.Lost == 1 ? "" : "s")}!"
                : $"{worst.Name}. It takes about {worst.AverageHealthLost:P0} of my health every fight.";
        }

        // A creature that beat him first and that he then beat is the best story.
        var comeback = foes.Where(f => f.Lost > 0 && f.Won > f.Lost).OrderByDescending(f => f.Won).FirstOrDefault();
        if (comeback is not null) {
            return $"{comeback.Name}! It beat me {comeback.Lost} time{(comeback.Lost == 1 ? "" : "s")} at first, but I've beaten it {comeback.Won} times since.";
        }

        var easy = foes.Where(f => f.Won >= 3 && f.Lost == 0).OrderByDescending(f => f.Won).FirstOrDefault();
        return easy is null ? null : $"{easy.Name}. I've beaten it {easy.Won} times and it never beat me!";
    }

    private static string SchoolOpinion(bool least) {
        var bySchool = UltimusFoes.All().Where(f => f.Fought > 0)
            .Select(f => (School: UltimusScout.SituationOf(f.Template).EnemySchool, Foe: f))
            .Where(x => !string.IsNullOrWhiteSpace(x.School))
            .GroupBy(x => x.School, StringComparer.OrdinalIgnoreCase)
            .Select(g => (School: g.Key, Lost: g.Sum(x => x.Foe.Lost), Won: g.Sum(x => x.Foe.Won), Hurt: g.Average(x => x.Foe.AverageHealthLost)))
            .ToList();
        if (bySchool.Count == 0) {
            return null;
        }

        if (least) {
            var worst = bySchool.OrderByDescending(s => s.Lost).ThenByDescending(s => s.Hurt).First();
            return worst.Lost > 0
                ? $"{Cap(worst.School)}. {Cap(worst.School)} creatures beat me {worst.Lost} time{(worst.Lost == 1 ? "" : "s")} so far!"
                : $"{Cap(worst.School)}. Its creatures hit me the hardest, about {worst.Hurt:P0} of my health a fight.";
        }

        var best = bySchool.Where(s => !s.School.Equals("Balance", StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Won - (s.Lost * 3)).FirstOrDefault();
        return best.School is null ? null
            : $"To fight? {Cap(best.School)}: I've beaten {best.Won} of its creatures" + (best.Lost == 0 ? " without a loss." : ".");
    }

    private string WorldOpinion(bool least) {
        var worlds = Worlds().Where(w => !string.IsNullOrWhiteSpace(w.World)).ToList();
        if (worlds.Count == 0) {
            return null;
        }

        // Time lost (standing, stuck) per world, from his time-waste map.
        _waste ??= ReadWaste();
        double Lost(string world) => _waste.Values.Where(w => WorldOf(w.Zone).Equals(world, StringComparison.OrdinalIgnoreCase)).Sum(w => w.Seconds) / 60;
        var rated = worlds.Select(w => (w.World, Lost: Lost(w.World), Hours: ((w.Finished ?? DateTime.UtcNow) - w.Started).TotalHours)).ToList();
        if (least) {
            var worst = rated.OrderByDescending(r => r.Lost).First();
            return worst.Lost < 5 ? null : $"{worst.World}. I lost about {worst.Lost:0} minutes there being stuck or waiting!";
        }

        if (rated.Count == 1) {
            return $"{rated[0].World}, the only world I know well so far! Ask me again when I've seen more.";
        }

        var best = rated.Where(r => !r.World.Contains("Wizard City", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Lost / Math.Max(0.5, r.Hours)).FirstOrDefault();
        return best.World is null ? null : $"{best.World}! Things went smoothly there.";
    }

    private string AreaOpinion(bool least) {
        _waste ??= ReadWaste();
        if (!least) {
            return null;
        }

        var worst = _waste.Values.GroupBy(w => w.Zone).Select(g => (Zone: g.Key, Minutes: g.Sum(w => w.Seconds) / 60)).OrderByDescending(z => z.Minutes).FirstOrDefault();
        return worst.Zone is null || worst.Minutes < 5 ? null : $"{UltimusText.Zone(worst.Zone)}. I've lost about {worst.Minutes:0} minutes there!";
    }

    private string QuestOpinion(bool least) {
        var runs = _runs.Values.Where(r => r.Expected > 0 && r.Seconds > 0).ToList();
        if (runs.Count == 0) {
            return null;
        }

        var pick = least ? runs.OrderByDescending(r => r.Seconds / r.Expected).First() : runs.OrderBy(r => r.Seconds / r.Expected).First();
        var minutes = pick.Seconds / 60;
        return least ? $"\"{pick.Title}\". It took me about {minutes:0} minutes, way longer than it should!"
            : $"\"{pick.Title}\"! I got through it in about {minutes:0} minutes, smooth.";
    }

    private static string PlayerOpinion(bool least) {
        if (least) {
            return null;
        }

        var best = BotSocial.Friends(UltimusRoster.NAME).Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .OrderByDescending(f => (f.BattlesTogether * 3) + f.Talks).FirstOrDefault();
        return best is null ? null
            : $"{best.Name}! We've talked {best.Talks} times" + (best.BattlesTogether > 0 ? $" and fought {best.BattlesTogether} battles together." : ".");
    }

    // "Do you like X?": X a world he was in, or a creature he fought; null when he doesn't know it.
    // The story worlds' codes (worlds.json, quest codes) and the names players use.
    private static readonly (string Code, string Name)[] s_worldNames = [
        ("WC", "Wizard City"), ("KT", "Krokotopia"), ("MB", "Marleybone"), ("MS", "MooShu"), ("DS", "Dragonspyre"), ("CL", "Celestia"),
        ("ZF", "Zafaria"), ("AV", "Avalon"), ("AZ", "Azteca"), ("KR", "Khrysalis"), ("KH", "Khrysalis"), ("PL", "Polaris"), ("MR", "Mirage"),
        ("EM", "Empyrea"), ("KM", "Karamelle"), ("LM", "Lemuria"), ("GH", "Grizzleheim"), ("WT", "Wintertusk"), ("WY", "Wysteria"),
    ];

    /// <summary>A world's name for its code ("KT" is Krokotopia), or the code itself.</summary>
    internal static string WorldName(string code)
        => s_worldNames.FirstOrDefault(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase)).Name ?? code ?? "";

    // The world a player means: by name ("wizard city", "krok", "mb") or code.
    private static string WorldMeant(string subject) {
        var s = subject.Trim();
        foreach (var (code, name) in s_worldNames) {
            if (s.Contains(name, StringComparison.OrdinalIgnoreCase) || string.Equals(s, code, StringComparison.OrdinalIgnoreCase)
                || (name.Length > 4 && s.Length >= 4 && name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) {
                return name;
            }
        }

        return null;
    }

    private string LikeOpinion(string subject) {
        if (subject.Length < 2) {
            return null;
        }

        var worldName = WorldMeant(subject);
        if (worldName is not null && Worlds().FirstOrDefault(w => WorldName(w.World) == worldName) is { } world) {
            _waste ??= ReadWaste();
            var lost = _waste.Values.Where(w => WorldOf(w.Zone).Equals(worldName, StringComparison.OrdinalIgnoreCase)).Sum(w => w.Seconds) / 60;
            var hours = ((world.Finished ?? DateTime.UtcNow) - world.Started).TotalHours;
            var memories = UltimusJournal.About(worldName);
            var good = memories.Count(m => m.Feeling > 0);
            var bad = memories.Count(m => m.Feeling < 0);
            var moment = memories.Where(m => m.Feeling != 0).OrderByDescending(m => m.At).FirstOrDefault();
            var feel = lost >= 20 || bad > good + 2
                ? $"It's okay! I lost about {lost:0} minutes in {worldName} being stuck or waiting, though."
                : $"I liked it! {worldName} has gone well for me" + (hours >= 1 ? $" in my {hours:0} hours there." : ".");
            if (world.Finished is not null) {
                feel += $" I finished its story at level {world.LevelOut}.";
            }

            return moment is null ? feel : $"{feel} I remember: {Lower(moment.Text)}";
        }

        if (worldName is not null) {
            return $"I haven't been to {worldName} yet, so I can't say!";
        }

        var foe = UltimusFoes.All().FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name.Contains(subject, StringComparison.OrdinalIgnoreCase));
        if (foe is not null) {
            return foe.Lost > foe.Won ? $"Not really, {foe.Name} beat me {foe.Lost} times!"
                : foe.Lost > 0 ? $"Now I do! {foe.Name} beat me {foe.Lost} times, but I've won {foe.Won}."
                : $"Sure, I've beaten {foe.Name} {foe.Won} times!";
        }

        // Anything else he has memories of: a quest, an area, a player.
        var about = UltimusJournal.About(subject);
        if (about.Count > 0) {
            var good = about.Count(m => m.Feeling > 0);
            var bad = about.Count(m => m.Feeling < 0);
            var latest = about.OrderByDescending(m => m.At).First();
            return (bad > good ? "Not my favorite. " : good > 0 ? "I liked it! " : "") + $"I remember: {Lower(latest.Text)} ({UltimusJournal.Ago(latest.At)})";
        }

        return null;
    }

    private static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    /// <summary>
    /// "Do you remember...?" from his journal: about one thing, his best or worst moment, today, or his latest moments.
    /// <paramref name="key"/> is "mood:subject" (mood: best, worst, today or empty).
    /// </summary>
    private static string Recall(string key) {
        var cut = key.IndexOf(':');
        var mood = cut < 0 ? "" : key[..cut];
        var subject = cut < 0 ? key : key[(cut + 1)..].Trim();
        if (UltimusJournal.Count == 0) {
            return "My journal is still empty. Ask me again once I've done a few quests!";
        }

        List<UltimusJournal.Entry> picked;
        if (subject.Length > 0) {
            picked = UltimusJournal.About(WorldMeant(subject) ?? subject).OrderByDescending(e => e.At).Take(2).ToList();
            if (picked.Count == 0) {
                return $"I don't remember anything about {subject}, sorry.";
            }
        }
        else if (mood == "best" || mood == "worst") {
            var all = UltimusJournal.Recent(1000);
            var pick = mood == "best"
                ? all.FirstOrDefault(e => e.Kind == UltimusJournal.WON && e.Text.StartsWith("Finally", StringComparison.Ordinal))
                  ?? all.FirstOrDefault(e => e.Kind == UltimusJournal.WORLD && e.Feeling > 0) ?? all.FirstOrDefault(e => e.Feeling > 0)
                : all.FirstOrDefault(e => e.Kind == UltimusJournal.LOST) ?? all.FirstOrDefault(e => e.Feeling < 0);
            return pick is null ? "I haven't had one yet!" : $"{(mood == "best" ? "My best moment" : "My worst moment")}: {Lower(pick.Text)} ({UltimusJournal.Ago(pick.At)})";
        }
        else {
            var since = mood == "today" ? DateTime.UtcNow.AddHours(-24) : DateTime.MinValue;
            picked = UltimusJournal.Recent(40).Where(e => e.At >= since && e.Kind != UltimusJournal.MET).Take(3).ToList();
            if (picked.Count == 0) {
                return mood == "today" ? "Quiet day so far, nothing worth writing down yet!" : "Nothing much worth remembering yet!";
            }
        }

        return "I remember: " + string.Join(" ", picked.Select(e => $"{e.Text.TrimEnd('.')} ({UltimusJournal.Ago(e.At)})."));
    }

    /// <summary>A fact about himself, from his real data ("what are you wearing?", "how many quests have you done?").</summary>
    private string Fact(UltimusMember.Status me, string key) {
        var wizard = me.Wizard;
        if (wizard is null) {
            return null;
        }

        switch (key) {
            case "gear": {
                var worn = new[] { Imlight.CoreLib.WizardData.Models.Player.EquipmentSlotType.Hat, Imlight.CoreLib.WizardData.Models.Player.EquipmentSlotType.Robe, Imlight.CoreLib.WizardData.Models.Player.EquipmentSlotType.Shoes,
                        Imlight.CoreLib.WizardData.Models.Player.EquipmentSlotType.Weapon }
                    .Select(slot => wizard.EquipmentBehavior?.GetItemInSlot(slot))
                    .Where(i => i is not null)
                    .Select(i => BotGear.Read((uint) Math.Min((ulong) i.m_templateID, uint.MaxValue))?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                return worn.Count == 0 ? "Just my starting clothes for now!" : $"I'm wearing {string.Join(", ", worn)}. I buy better gear when it's a real upgrade.";
            }
            case "deck": {
                var spells = wizard.SpellbookBehavior?.LearnedSpellTemplateIds?.Select(id => Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate((uint) id))
                    .OfType<Imcodec.ObjectProperty.TypeCache.SpellTemplate>().Select(t => (string) t.m_displayName)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Contains(' ') || !n.Contains('_') ? n : Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(n))
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(8).ToList() ?? [];
                return spells.Count == 0 ? "Not many spells yet!" : $"My spells include {string.Join(", ", spells)}.";
            }
            case "stats": {
                var hours = Worlds().Sum(w => ((w.Finished ?? DateTime.UtcNow) - w.Started).TotalHours);
                var foes = UltimusFoes.All();
                return $"I've played the story for about {hours:0} hours, fought {foes.Sum(f => f.Fought)} battles (won {foes.Sum(f => f.Won)})" +
                       $", and finished {_today.GetValueOrDefault("quests finished")} quests today.";
            }
            case "xp": {
                var school = wizard.MagicSchoolBehavior;
                return school is null ? null : $"I'm level {school.Level} with {school.ExperiencePoints:N0} XP. All of it from quests and their fights!";
            }
            case "next": {
                var at = s_worldNames.Select(w => w.Name).ToList();
                var now = WorldOf(me.Zone);
                var i = at.IndexOf(now);
                return i >= 0 && i + 1 < at.Count && i < 15 ? $"After {now}, it's on to {at[i + 1]}!" : "Wherever the story takes me next!";
            }
            case "pet":
                return "I have a pet with me, but I never train pets. I just quest!";
            case "mount":
                return "Yes, I ride my Ghulture! It was a gift from the school.";
            case "potions": {
                var stats = wizard.GameStats;
                return stats is null ? null : $"I have {stats.m_potionCharge:0} of {stats.m_potionMax:0} potions.";
            }
            default: {
                var stats = wizard.GameStats;
                return stats is null ? null
                    : $"I have {stats.m_currentHitpoints:N0} of {stats.m_baseHitpoints + stats.m_bonusHitpoints:N0} health and {stats.m_currentMana} of {stats.m_baseMana + stats.m_bonusMana} mana.";
            }
        }
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

}
