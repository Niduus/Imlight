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

    internal static void Fill(Wizard wizard, string school, BotStyle style) {
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
            // What his gear gives, so each socket gets what he needs most (BotJewelPicker).
            var need = BotJewelPicker.For(wizard, school, style);
            var petTalents = TrueFormMaker.PetTalents(wizard, school, style);
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
                    var nowScore = nowId == 0 ? double.MinValue : Score(now, shape.Value, pet, need, petTalents, school, style);
                    if (nowId != 0) {
                        JewelSockets.Unsocket(item, socket);
                    }

                    var best = jewels.Where(j => j.Shape == shape.Value).Select(j => (Jewel: j, Score: Score(j, shape.Value, pet, need, petTalents, school, style)))
                        .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Jewel.Id)
                        .FirstOrDefault(x => JewelSockets.CheckSocket(wizard, item, x.Jewel.Template, socket) is null);
                    // Nothing for this setup fits (a universal or glass setup's tear and square sockets, which only take
                    // health or resist): the socket still gets the jewel that helps him most overall, never left open
                    // (Niduus, 2026-11-20: "open sockets that can clearly have jewels in to help his build, needs to be
                    // looked into").
                    if (best.Jewel is null && nowScore <= 0) {
                        best = jewels.Where(j => j.Shape == shape.Value)
                            .Select(j => (Jewel: j, Score: Score(j, shape.Value, pet, need, petTalents, school, style, fallback: true)))
                            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Jewel.Id)
                            .FirstOrDefault(x => JewelSockets.CheckSocket(wizard, item, x.Jewel.Template, socket) is null);
                        nowScore = nowId == 0 ? double.MinValue : Score(now, shape.Value, pet, need, petTalents, school, style, fallback: true);
                    }

                    if (best.Jewel is not null && best.Score > nowScore) {
                        JewelSockets.Socket(item, socket, best.Jewel.Id);
                        changed |= best.Jewel.Id != nowId;
                        put += best.Jewel.Id != nowId ? 1 : 0;
                        if (best.Jewel.Id != nowId) {
                            Logger.Information("True Form: put {0} in his {1} ({2} socket).",
                                Logger.Args(best.Jewel.Name, BotJewels.NameOf(template), JewelSockets.ShapeName(shape.Value)));
                        }
                    }
                    else if (nowId != 0 && nowScore > 0) {
                        // Kept only while it still helps (a pet jewel that repeats a talent, or one for another setup, comes out).
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

    // How good a jewel is for him: its stats as his gear is scored; a pet jewel by what its talent really gives him
    // (BotPetTalentStats), only a talent his pet does not have yet, so the jewel is a real sixth.
    private static double Score(BotJewels.Jewel jewel, JewelSocketType shape, ClientPetItemBehavior pet, BotJewelPicker need,
        List<BotPetTalentStats.Info> petTalents, string school, BotStyle style, bool fallback = false) {
        if (jewel is null) {
            return 0;
        }

        if (jewel.Shape != JewelSocketType.SOCKETTYPE_PET) {
            return fallback ? need.Fallback(jewel) : need.Score(jewel, shape);
        }

        // Never a talent his pet has already, revealed or in his pool, by name or by what it gives (Niduus, 2026-11-19: a
        // Pain-Giver jewel on a pet with Pain-Giver: "2 Pain-Givers don't work like that"; 2026-11-20: "Bot can NOT have
        // jewel of pet talent that is already in pool and revealed"). A jewel talent that gives the same kind of stat for
        // the same school as one of his talents (Spell-Defying +5 resist beside Spell-Proof +10 resist) counts as had too.
        var ids = (pet?.m_expressedTalents ?? []).Concat(pet?.m_allTalents ?? []).ToHashSet();
        var has = ids.Select(id => PetData.GetTalent(id)?.m_talentName).Where(k => k is not null)
            .Select(k => BotPetTalentStats.Of(k)).Where(i => i is not null).ToList();
        var best = 0.0;
        foreach (var key in jewel.TalentKeys) {
            var info = BotPetTalentStats.Of(key);
            if (info is null) {
                continue;
            }

            // A two-talent jewel (Lifesaver Jade: +4% outgoing and May Cast Pigsie) is out as a whole when either half repeats
            // one of his talents (Niduus, 2026-11-20: "we really gotta make him spot the duo stat jewels next").
            if (ids.Contains(PetData.TalentId(key)) || has.Any(h => SameTalent(h, info))) {
                return 0;
            }

            best += BotPetTalentStats.Score(info, school, fallback ? BotStyle.Solo : style, need.AccuracyFull, need.PipsFull);
        }

        return best;
    }

    // Same talent for the jewel rule: same shown name, or any stat of the same kind for the same school.
    private static bool SameTalent(BotPetTalentStats.Info had, BotPetTalentStats.Info jewel) =>
        had.Shown == jewel.Shown
        || had.Stats.Any(h => jewel.Stats.Any(j => j.Kind == h.Kind && j.School == h.School));

}
