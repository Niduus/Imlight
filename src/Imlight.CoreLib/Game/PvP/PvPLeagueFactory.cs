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
 * Loads the league templates, the season templates and the Elo rank table from the PvP directory of the
 * Root.wad, and looks leagues and seasons up by name or by the hash the client sends.
 * 
 * USAGE EXAMPLE:
 * if (PvPLeagueFactory.TryGetLeague(message.LeagueID, out var league)) { }
 * 
 * NOTE:
 * League and season ids are StringHash.Compute of m_leagueType and m_seasonName.
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
using System.Globalization;
using System.Text.RegularExpressions;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.PvP;

internal sealed partial class PvPLeagueFactory : RootDirectoryResourceSingleton<PvPLeagueFactory> {

    protected override string DirectoryName => "PvP/";

    private static Dictionary<string, PvPLeagueTemplate> s_leaguesByName = new(StringComparer.Ordinal);
    private static Dictionary<uint, PvPLeagueTemplate> s_leaguesById = [];
    private static Dictionary<string, PvPSeasonEntry> s_seasonsByName = new(StringComparer.Ordinal);
    private static Dictionary<uint, PvPSeasonEntry> s_seasonsById = [];
    private static EloConfig s_eloConfig;

    [GeneratedRegex(@"/Seasons(?<year>\d{4})/", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonYearPattern();

    [GeneratedRegex(@"_(?<month>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonNameMonthPattern();

    protected override void AfterLoad() {
        var leaguesByName = new Dictionary<string, PvPLeagueTemplate>(StringComparer.Ordinal);
        var leaguesById = new Dictionary<uint, PvPLeagueTemplate>();
        var seasonsByName = new Dictionary<string, PvPSeasonEntry>(StringComparer.Ordinal);
        var seasonsById = new Dictionary<uint, PvPSeasonEntry>();
        EloConfig eloConfig = null;

        foreach (var file in base.Files) {
            if (!PvPResourceReader.TryRead<PropertyClass>(file.Key, file.Value, out var template)) {
                continue;
            }

            switch (template) {
                case PvPLeagueTemplate league: {
                    string name = league.m_leagueType;
                    if (!leaguesByName.TryAdd(name, league) || !leaguesById.TryAdd(StringHash.Compute(name), league)) {
                        Logger.Error("League {Name} in {File} is defined twice.", Logger.Args(name, file.Key.FileName));
                    }

                    break;
                }
                case PvPSeasonTemplate season: {
                    string name = season.m_seasonName;
                    var entry = new PvPSeasonEntry(season, GetStartYear(season, file.Key.FileName));
                    if (!seasonsByName.TryAdd(name, entry) || !seasonsById.TryAdd(StringHash.Compute(name), entry)) {
                        Logger.Error("Season {Name} in {File} is defined twice.", Logger.Args(name, file.Key.FileName));
                    }

                    break;
                }
                case EloConfig elo: {
                    if (eloConfig is not null) {
                        Logger.Error("The Elo config in {File} is a second one.", Logger.Args(file.Key.FileName));
                    }

                    eloConfig = elo;

                    break;
                }
                default: {
                    Logger.Error("PvP file {File} is a {Type}, which no loader handles.",
                        Logger.Args(file.Key.FileName, template.GetType().Name));

                    break;
                }
            }
        }

        s_leaguesByName = leaguesByName;
        s_leaguesById = leaguesById;
        s_seasonsByName = seasonsByName;
        s_seasonsById = seasonsById;
        s_eloConfig = eloConfig;

        Logger.Information("Loaded {LeagueCount} PvP leagues and {SeasonCount} seasons.", Logger.Args(leaguesByName.Count, seasonsByName.Count));
        if (eloConfig is null) {
            Logger.Error("The PvP Elo config is missing from the Root.wad.");
        }
    }

    private static int? GetStartYear(PvPSeasonTemplate season, string fileName) {
        var yearMatch = SeasonYearPattern().Match(fileName ?? string.Empty);
        if (!yearMatch.Success) {
            return null;
        }

        var directoryYear = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);

        // The name carries the month the season starts in, so PvP_2026_01 starts in late December of 2025.
        var monthMatch = SeasonNameMonthPattern().Match(season.m_seasonName.ToString());
        if (monthMatch.Success && season.m_startMonth - int.Parse(monthMatch.Groups["month"].Value, CultureInfo.InvariantCulture) > 6) {
            return directoryYear - 1;
        }

        return directoryYear;
    }

    internal static bool TryGetLeague(string name, out PvPLeagueTemplate league) {
        league = null;

        return name is not null && s_leaguesByName.TryGetValue(name, out league);
    }

    internal static bool TryGetLeague(uint leagueId, out PvPLeagueTemplate league)
        => s_leaguesById.TryGetValue(leagueId, out league);

    internal static bool TryGetSeason(string name, out PvPSeasonEntry season) {
        season = null;

        return name is not null && s_seasonsByName.TryGetValue(name, out season);
    }

    internal static bool TryGetSeason(uint seasonId, out PvPSeasonEntry season)
        => s_seasonsById.TryGetValue(seasonId, out season);

    internal static bool TryGetEloConfig(out EloConfig eloConfig) {
        eloConfig = s_eloConfig;

        return eloConfig is not null;
    }

    internal static IReadOnlyCollection<PvPLeagueTemplate> GetLeagues()
        => s_leaguesByName.Values;

    internal static IReadOnlyCollection<PvPSeasonEntry> GetSeasons()
        => s_seasonsByName.Values;

}
