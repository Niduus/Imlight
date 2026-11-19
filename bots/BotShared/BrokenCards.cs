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
using System.Collections.Concurrent;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.Setup;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The cards Ahziael marks BROKEN on its Spells tab (the same check, SpellSupport), so Ultimus, the Quest Team and the
/// Bot Players never pick one (Niduus, 2026-11-16: "Never picking broken cards, report problems with cards"). Each broken
/// card a bot would have used is reported once in the server log, with why.
/// </summary>
internal static class BrokenCards {

    private static readonly ConcurrentDictionary<uint, string> s_why = new();

    /// <summary>Why the server can't do this card, or null when it works (or partly works).</summary>
    internal static string Why(uint spellId) {
        if (spellId == 0) {
            return null;
        }

        var why = s_why.GetOrAdd(spellId, id => {
            try {
                if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate template) {
                    return "";
                }

                var result = SpellSupport.Classify(template, (string) template.m_name ?? "", "");

                return result.Status == SpellStatus.Broken ? result.Reason ?? "broken" : "";
            }
            catch (Exception) {
                return "";
            }
        });

        return why.Length == 0 ? null : why;
    }

    private static readonly ConcurrentDictionary<uint, byte> s_told = new();

    /// <summary>Whether the card is broken; the first time, a CARD PROBLEM line in the server log says which and why.</summary>
    internal static bool Skip(uint spellId) {
        if (Why(spellId) is not { } why) {
            return false;
        }

        if (s_told.TryAdd(spellId, 0)) {
            Logger.Warning("CARD PROBLEM: {0} ({1}) is marked BROKEN, as on Ahziael's Spells tab: {2} The bots leave it out of their decks.",
                Logger.Args(Imlight.CoreLib.Game.Ultimus.UltimusText.SpellName(spellId), spellId, why));
        }

        return true;
    }

}
