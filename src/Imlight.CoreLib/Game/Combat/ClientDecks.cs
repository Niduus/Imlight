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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// The decks the game client ships: DeckTemplate files under Root.wad's Decks/ folder, one spell name per card
/// (Decks/Battlegrounds/MDeck-D-BG-Polymorph-WolfWarrior-A-01.xml). A creature's deck is named by its DeckBehavior
/// (m_defaultDeck); SpiralDB has most of them, but not, for one, the Beastmoon arenas' (Game/Battlegrounds), so their
/// creatures played a stand-in deck. A deck SpiralDB lacks is read from here, from the player's own game files.
/// </summary>
internal static class ClientDecks {

    private const string FOLDER = "Decks/";

    private static readonly object s_lock = new();
    private static Dictionary<string, string> s_paths;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> s_decks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The spell names of a client deck, one per card, or null when the client has no deck by that name.</summary>
    internal static IReadOnlyList<string> SpellNames(string deckName) {
        if (string.IsNullOrWhiteSpace(deckName)) {
            return null;
        }

        if (s_decks.TryGetValue(deckName, out var known)) {
            return known;
        }

        var path = Path(deckName);
        var deck = path is null ? null : RootArchiveLoader.GetFile<DeckTemplate>(path);
        var names = deck?.m_spellNameList?.Select(n => (string) n).Where(n => !string.IsNullOrEmpty(n)).ToList();
        s_decks[deckName] = names;

        return names;
    }

    // Where a deck's file is: the deck files are spread over Decks/'s sub-folders, so they are indexed by name once.
    private static string Path(string deckName) {
        lock (s_lock) {
            if (s_paths is null) {
                s_paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var wad = RootArchiveLoader.GetRootWad();
                foreach (var file in wad?.Files.Keys ?? Enumerable.Empty<string>()) {
                    if (file.StartsWith(FOLDER, StringComparison.OrdinalIgnoreCase) && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
                        var name = file[(file.LastIndexOf('/') + 1)..^".xml".Length];
                        s_paths.TryAdd(name, file);
                    }
                }

                Logger.Information("Client decks: {0} deck files in the game's Decks folder.", Logger.Args(s_paths.Count));
            }

            return s_paths.TryGetValue(deckName, out var path) ? path : null;
        }
    }

}
