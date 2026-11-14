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
using System.Linq;
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
    private string LikeOpinion(string subject) {
        if (subject.Length < 3) {
            return null;
        }

        if (Worlds().FirstOrDefault(w => w.World.Contains(subject, StringComparison.OrdinalIgnoreCase) || subject.Contains(w.World, StringComparison.OrdinalIgnoreCase)) is { } world) {
            _waste ??= ReadWaste();
            var lost = _waste.Values.Where(w => WorldOf(w.Zone).Equals(world.World, StringComparison.OrdinalIgnoreCase)).Sum(w => w.Seconds) / 60;
            var hours = ((world.Finished ?? DateTime.UtcNow) - world.Started).TotalHours;
            return lost >= 20 ? $"It's okay! I lost about {lost:0} minutes in {world.World} being stuck or waiting, though."
                : $"Yes! {world.World} has gone well for me" + (hours >= 1 ? $" in my {hours:0} hours there." : ".");
        }

        var foe = UltimusFoes.All().FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Name) && f.Name.Contains(subject, StringComparison.OrdinalIgnoreCase));
        if (foe is not null) {
            return foe.Lost > foe.Won ? $"Not really, {foe.Name} beat me {foe.Lost} times!"
                : foe.Lost > 0 ? $"Now I do! {foe.Name} beat me {foe.Lost} times, but I've won {foe.Won}."
                : $"Sure, I've beaten {foe.Name} {foe.Won} times!";
        }

        return null;
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

}
