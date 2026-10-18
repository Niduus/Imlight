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
 *
 * ========================================================================
 * PVP
 * ========================================================================
 * 
 * PURPOSE:
 * Loads the match templates (team sizes, queue timings, arena zones) and the level modifier table that
 * rates a player for matchmaking, and looks them up by name or by MatchNameID.
 * 
 * USAGE EXAMPLE:
 * if (PvPMatchFactory.TryGetMatch(message.MatchNameID, out var match)) { }
 * 
 * NOTE:
 * The id the client sends is StringHash.Compute of m_matchName. PvPLevelModifiers.xml is not a match;
 * it is the TournamentLevelModifiers table that m_levelModifiersListID names.
 * 
 * TODO:
 * 
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.PvP;

internal sealed class PvPMatchFactory : RootDirectoryResourceSingleton<PvPMatchFactory> {

    protected override string DirectoryName => "Matches/";

    private static Dictionary<string, MatchTemplate> s_matchesByName = new(StringComparer.Ordinal);
    private static Dictionary<uint, MatchTemplate> s_matchesById = [];
    private static Dictionary<ulong, TournamentLevelModifiers> s_levelModifiers = [];

    protected override void AfterLoad() {
        var byName = new Dictionary<string, MatchTemplate>(StringComparer.Ordinal);
        var byId = new Dictionary<uint, MatchTemplate>();
        var levelModifiers = new Dictionary<ulong, TournamentLevelModifiers>();

        foreach (var file in base.Files) {
            if (!PvPResourceReader.TryRead<PropertyClass>(file.Key, file.Value, out var template)) {
                continue;
            }

            switch (template) {
                case MatchTemplate match: {
                    string name = match.m_matchName;
                    if (!byName.TryAdd(name, match) || !byId.TryAdd(StringHash.Compute(name), match)) {
                        Logger.Error("Match {Name} in {File} is defined twice.", Logger.Args(name, file.Key.FileName));
                    }

                    break;
                }
                case TournamentLevelModifiers modifiers: {
                    if (!levelModifiers.TryAdd(modifiers.m_templateID, modifiers)) {
                        Logger.Error("Level modifiers {Id} in {File} are defined twice.",
                            Logger.Args(modifiers.m_templateID, file.Key.FileName));
                    }

                    break;
                }
                default: {
                    Logger.Error("PvP match file {File} is a {Type}, which no loader handles.",
                        Logger.Args(file.Key.FileName, template.GetType().Name));

                    break;
                }
            }
        }

        s_matchesByName = byName;
        s_matchesById = byId;
        s_levelModifiers = levelModifiers;

        Logger.Information("Loaded {MatchCount} PvP matches and {TableCount} level modifier tables.",
            Logger.Args(byName.Count, levelModifiers.Count));
    }

    internal static bool TryGetMatch(string name, out MatchTemplate match) {
        match = null;

        return name is not null && s_matchesByName.TryGetValue(name, out match);
    }

    internal static bool TryGetMatch(uint nameId, out MatchTemplate match)
        => s_matchesById.TryGetValue(nameId, out match);

    internal static bool TryGetLevelModifiers(ulong templateId, out TournamentLevelModifiers modifiers)
        => s_levelModifiers.TryGetValue(templateId, out modifiers);

    internal static IReadOnlyCollection<MatchTemplate> GetMatches()
        => s_matchesByName.Values;

}
