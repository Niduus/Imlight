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
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's jewels (Niduus, 2026-11-19: "Give him jewels that would be appropriate for his play style"): every socket of
/// what he wears gets the best jewel or pin of its shape a player can get in the real game (BotJewels), scored as his gear
/// is (BotGear, the Backup style: damage, pierce and resist first, with health and healing, as he hits, shields and heals).
/// His pet's socket gets a jewel with one of his pet talents the pet does not have yet. A socket keeps its jewel unless a
/// better one fits. Done when he is made ready, after he is dressed, so a new piece of gear gets its jewels too.
/// </summary>
internal static class TrueFormJewels {

    private const string JEWELS_FILE = "jewels.txt";

    internal static void Fill(Wizard wizard) {
        try {
            // The game's books are read in the background: up to a minute for them.
            var jewels = BotJewels.All;
            for (var i = 0; i < 120 && jewels.Count == 0; i++) {
                System.Threading.Thread.Sleep(500);
                jewels = BotJewels.All;
            }

            if (jewels.Count == 0) {
                Logger.Warning("True Form: the game's jewels could not be read; his sockets stay as they are.");
                return;
            }

            var lines = new List<string>();
            var put = 0;
            // His Myth damage and pierce from what he wears, to pick a circle socket's jewel by what he needs most.
            var need = Need.Of(wizard);
            foreach (var item in (wizard.EquipmentBehavior?.EquippedItems ?? []).ToList()) {
                var template = ItemHelper.GetItemTemplate(item);
                var sockets = JewelSockets.GetSockets(template);
                if (sockets.Count == 0) {
                    continue;
                }

                var pet = PetProgress.GetBehavior(item);
                var state = JewelSockets.GetState(item, create: true);
                if (state?.m_publicJewelInfo is { } info) {
                    info.m_bSocketsLocked = false;
                }

                var changed = false;
                for (var socket = 0; socket < sockets.Count; socket++) {
                    var shape = sockets[socket]?.m_socketType;
                    if (shape is null) {
                        continue;
                    }

                    var nowId = JewelSockets.GetPairs(item).FirstOrDefault(p => p?.m_socketNumber == socket)?.m_jewelTemplateID ?? 0;
                    var now = jewels.FirstOrDefault(j => j.Id == nowId);
                    var circle = shape.Value == JewelSocketType.SOCKETTYPE_CIRCLE;
                    var nowScore = nowId == 0 ? double.MinValue : circle ? need.Score(now) : Score(now, pet);
                    if (nowId != 0) {
                        JewelSockets.Unsocket(item, socket);
                    }

                    var best = jewels.Where(j => j.Shape == shape.Value).Select(j => (Jewel: j, Score: circle ? need.Score(j) : Score(j, pet)))
                        .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Jewel.Id)
                        .FirstOrDefault(x => JewelSockets.CheckSocket(wizard, item, x.Jewel.Template, socket) is null);
                    if (best.Jewel is not null && best.Score > nowScore) {
                        JewelSockets.Socket(item, socket, best.Jewel.Id);
                        changed |= best.Jewel.Id != nowId;
                        put += best.Jewel.Id != nowId ? 1 : 0;
                        if (best.Jewel.Id != nowId) {
                            Logger.Information("True Form: put {0} in his {1} ({2} socket).",
                                Logger.Args(best.Jewel.Name, BotJewels.NameOf(template), JewelSockets.ShapeName(shape.Value)));
                        }
                    }
                    else if (nowId != 0) {
                        JewelSockets.Socket(item, socket, (uint) nowId);
                    }

                    var inIt = JewelSockets.GetPairs(item).FirstOrDefault(p => p?.m_socketNumber == socket)?.m_jewelTemplateID ?? 0;
                    need.Add(inIt == 0 ? null : jewels.FirstOrDefault(j => j.Id == inIt));
                    lines.Add($"{BotJewels.NameOf(template)}, {JewelSockets.ShapeName(shape.Value)} socket: "
                              + (inIt == 0 ? "empty (no jewel fits)" : BotJewels.NameOf(CoreObjectFactory.GetCoreTemplate((uint) inIt) as WizItemTemplate)));
                }

                if (changed) {
                    WizardItemCollection.SaveItemBehaviors(item);
                    if (pet is not null) {
                        PetCare.Finish(wizard, item, null, _ => { });
                    }
                }
            }

            EquipmentExtras.Refresh(wizard);
            try {
                Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
                File.WriteAllLines(Path.Combine(UltimusSeat.TrueForm.Folder, JEWELS_FILE), lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("True Form: could not write his jewel list: {0}", Logger.Args(e.Message));
            }

            Logger.Information("True Form: his sockets are filled ({0} jewels put in, {1} sockets in all).", Logger.Args(put, lines.Count));
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not give him his jewels: {0}", Logger.Args(e.Message));
        }
    }

    // A circle socket takes a % damage or a pierce jewel (Niduus, 2026-11-19: "circle is a toss out because it can be % damage
    // or pierce"): the one that raises his hits the most now. A damage point adds to all his damage (100 + damage);
    // a pierce point takes a point off a top level enemy's resist (60%) until his pierce is past it.
    private sealed class Need {

        private const double ENEMY_RESIST = 60;

        private double _damage;
        private double _pierce;

        internal static Need Of(Wizard wizard) {
            var need = new Need();
            foreach (var item in wizard.EquipmentBehavior?.EquippedItems ?? []) {
                need.Add(BotGear.StatsOf(ItemHelper.GetItemTemplate(item)));
            }

            return need;
        }

        internal void Add(BotJewels.Jewel jewel) => Add(jewel?.Stats);

        private void Add(IEnumerable<BotGear.Stat> stats) {
            foreach (var stat in stats ?? []) {
                if (!Counts(stat)) {
                    continue;
                }

                if (stat.Kind == "Damage") {
                    _damage += stat.Value;
                }
                else if (stat.Kind == "ArmorPiercing") {
                    _pierce += stat.Value;
                }
            }
        }

        private static bool Counts(BotGear.Stat stat) => stat.School.Length == 0 || stat.School.Equals("Myth", StringComparison.OrdinalIgnoreCase);

        // How much more his hits do with this jewel, in hundredths of a percent; other stats only break a tie.
        internal double Score(BotJewels.Jewel jewel) {
            if (jewel is null) {
                return 0;
            }

            var gain = 0.0;
            foreach (var stat in jewel.Stats.Where(Counts)) {
                if (stat.Kind == "Damage") {
                    gain += stat.Value / (100 + _damage);
                }
                else if (stat.Kind == "ArmorPiercing") {
                    var left = Math.Max(0, ENEMY_RESIST - _pierce);
                    gain += Math.Min(stat.Value, left) / (100 - left);
                }
            }

            var rest = BotGear.ScoreStats(JewelStats(jewel.Stats).Where(s => s.Kind is not ("Damage" or "ArmorPiercing")), "Myth", BotStyle.Backup);
            return (gain * 10000) + (rest * 0.01);
        }
    }

    // The stats a jewel is picked by (Niduus, 2026-11-19: "flat damage and flat resist is not usually sought after. Look for
    // the % damage jewel and maybe universal flat resist for squares"): flat damage does not count, flat resist only when it
    // is to every school and then well under a % damage jewel, so a % damage jewel wins wherever one fits.
    private static IEnumerable<BotGear.Stat> JewelStats(IEnumerable<BotGear.Stat> stats) {
        foreach (var stat in stats ?? []) {
            switch (stat.Kind) {
                case "FlatDamage":
                    continue;
                case "FlatReduceDamage":
                    if (stat.School.Length == 0) {
                        yield return stat with { Value = stat.Value * 0.25f };
                    }

                    continue;
                default:
                    yield return stat;
                    break;
            }
        }
    }

    // How good a jewel is for him: its stats as his gear is scored; a pet jewel by his pet talents (first ones best), only
    // a talent his pet does not have yet.
    private static double Score(BotJewels.Jewel jewel, ClientPetItemBehavior pet) {
        if (jewel is null) {
            return 0;
        }

        if (jewel.Shape != JewelSocketType.SOCKETTYPE_PET) {
            return BotGear.ScoreStats(JewelStats(jewel.Stats), "Myth", BotStyle.Backup);
        }

        var best = 0.0;
        // A talent his role uses that is not one of his five (his five come first: PetTalents); under 1, so one of his
        // named talents always wins: myth or every-school damage, resist, pierce, critical, health, healing taken.
        foreach (var key in jewel.TalentKeys) {
            if (pet is not null && pet.m_expressedTalents.Contains(PetData.TalentId(key))) {
                continue;
            }

            var tier = key.Length > 0 && char.IsDigit(key[^1]) ? key[^1] - '0' : 1;
            var kind = key.StartsWith("Talent-Damage-Myth", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Talent-Damage-All", StringComparison.OrdinalIgnoreCase) ? 0.9
                : key.StartsWith("Talent-Resist-All", StringComparison.OrdinalIgnoreCase) ? 0.85
                : key.StartsWith("Talent-ArmorPiercing", StringComparison.OrdinalIgnoreCase) ? 0.8
                : key.StartsWith("Talent-CriticalHit-Myth", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Talent-CriticalHit-All", StringComparison.OrdinalIgnoreCase) ? 0.7
                : key.StartsWith("Talent-Health", StringComparison.OrdinalIgnoreCase) ? 0.6
                : key.StartsWith("Talent-IncHealing", StringComparison.OrdinalIgnoreCase) ? 0.5
                : 0;
            best = Math.Max(best, kind * (0.9 + (0.025 * Math.Min(tier, 4))));
        }

        foreach (var talent in jewel.Talents) {
            var index = Array.FindIndex(TrueFormMaker.PetTalents, n => n.Equals(talent, StringComparison.OrdinalIgnoreCase));
            if (index < 0) {
                continue;
            }

            var has = pet is not null && PetData.AllTalents.FirstOrDefault(t => string.Equals(Locale.GetEnglishName(t.m_displayName), talent, StringComparison.OrdinalIgnoreCase)) is { } known
                      && pet.m_expressedTalents.Contains(PetData.TalentId(known.m_talentName));
            if (!has) {
                best = Math.Max(best, TrueFormMaker.PetTalents.Length - index);
            }
        }

        return best;
    }

}
