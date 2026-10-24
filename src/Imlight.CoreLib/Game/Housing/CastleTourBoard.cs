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
 * HOUSING SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * Castle Tours: the houses players put on the Tour, the ratings they get, and every player's favorites and rating
 * badge credit. Everything is kept in castle-tours.json next to the server, which is read again when it changes.
 *
 * USAGE EXAMPLE:
 * CastleTourBoard.TryAdd(wizard, templateId, CastleTourType.Decorated, out var status);
 * var top = CastleTourBoard.Leaderboard(wizard, CastleTourBoard.TopRatedBoard);
 *
 * NOTE:
 * - A house on the Tour is named by its owner and its deed template, never by its lot: the lot of a house that was
 *   never entered does not exist yet. A deed's structure (the inside) is on the Tour with it.
 * - The house types are the client's (CastleToursVisitTypeWindow buttons, 0x140f04830): 0 Decorated, 1 Magical,
 *   2 Crazy, 3 Maze, 4 Seasonal, 5 Dorm, 6 Gauntlet, 7 Collection; 8 is Any.
 * - The leaderboards are the CastleToursSelectTopCastle window's (0x140f07c10): 0-7 the top houses of a type, 8 the
 *   top houses of all, 9 friends, 10 favorites, 11 nominated for the Hall of Fame. The client asks with 0x80 added
 *   for "Show My Castles".
 * - The rating badges (CastleRating-001 to -004) and their counts are guesses: the game files have only their
 *   names. One house gives one credit a day and a player gets at most DailyBadgeCredit a day.
 *
 * TODO:
 * - The live game asks for 50 items in every area of a house before it can join the Tour (Housing_00000747).
 *   This server lets any house join.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// A house type of Castle Tours, as the client numbers them.
/// </summary>
internal enum CastleTourType : sbyte {
    Decorated = 0,
    Magical = 1,
    Crazy = 2,
    Maze = 3,
    Seasonal = 4,
    Dorm = 5,
    Gauntlet = 6,
    Collection = 7,
    Any = 8,
}

/// <summary>
/// One house on the Tour.
/// </summary>
internal sealed class CastleTourHouse {
    public ulong OwnerCharId { get; set; }
    /// <summary>The owner's name, for the log and the Manager.</summary>
    public string OwnerName { get; set; } = "";
    /// <summary>The owner's packed name as the client reads it (spaced hex).</summary>
    public string OwnerNameHex { get; set; } = "";
    public uint TemplateId { get; set; }
    public uint StructureTemplateId { get; set; }
    public CastleTourType Type { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime EndsUtc { get; set; }
    public int Visits { get; set; }
    /// <summary>The ratings by character id: 1 good, 2 great, 3 fantastic, 4 spectacular.</summary>
    public Dictionary<string, int> Ratings { get; set; } = [];

    [JsonIgnore]
    internal double Average => Ratings.Count == 0 ? 0 : Ratings.Values.Average();

    internal bool Names(ulong ownerCharId, uint templateId)
        => OwnerCharId == ownerCharId && templateId != 0 && (TemplateId == templateId || StructureTemplateId == templateId);
}

/// <summary>
/// A favorite house of a player. It stays a favorite when it leaves the Tour, but cannot be visited then.
/// </summary>
internal sealed class CastleTourFavorite {
    public ulong OwnerCharId { get; set; }
    public uint TemplateId { get; set; }
}

/// <summary>
/// What a player did on the Tour.
/// </summary>
internal sealed class CastleTourPlayer {
    public List<CastleTourFavorite> Favorites { get; set; } = [];
    /// <summary>All rating badge credit so far.</summary>
    public int Credit { get; set; }
    /// <summary>The day (yyyy-MM-dd, server time) of <see cref="CreditToday"/>.</summary>
    public string CreditDay { get; set; } = "";
    public int CreditToday { get; set; }
    /// <summary>The houses ("owner:template") that gave credit on <see cref="CreditDay"/>.</summary>
    public List<string> CreditedToday { get; set; } = [];
}

/// <summary>
/// The settings of Castle Tours, kept at the top of castle-tours.json.
/// </summary>
internal sealed class CastleTourSettings {
    /// <summary>How many days a house stays on the Tour; the add window shows it.</summary>
    public int Days { get; set; } = 30;
    /// <summary>The most rating badge credit a player gets in a day.</summary>
    public int DailyBadgeCredit { get; set; } = 5;
    /// <summary>The credit that earns CastleRating-001, -002, -003 and -004.</summary>
    public List<int> BadgeCredit { get; set; } = [5, 25, 50, 100];
    /// <summary>The most favorites a player keeps.</summary>
    public int MaxFavorites { get; set; } = 50;
}

internal sealed class CastleTourFile {
    public CastleTourSettings Settings { get; set; } = new();
    public List<CastleTourHouse> Houses { get; set; } = [];
    /// <summary>By character id.</summary>
    public Dictionary<string, CastleTourPlayer> Players { get; set; } = [];
}

/// <summary>
/// Why a house could not join or leave the Tour, as the client's MSG_CASTLETOURSADDRESULT and
/// MSG_CASTLETOURSREMOVERESULT number it (CastleToursBehavior, 0x140efad50 and 0x140efbbe0).
/// </summary>
internal enum CastleTourResult : uint {
    Problem = 0,
    Done = 1,
    Disabled = 2,
    NeedsMoreItems = 3,
}

/// <summary>
/// What MSG_CASTLETOURSADDFAVORITE tells the client (ClientHousingManager, 0x140f4b890).
/// </summary>
internal enum CastleTourFavoriteResult : sbyte {
    Added = 0,
    Problem = 1,
    NoSpace = 2,
}

internal static class CastleTourBoard {

    internal const string FILE = "castle-tours.json";

    /// <summary>The leaderboard of the top houses of all types.</summary>
    internal const int TopRatedBoard = 8;
    internal const int FriendsBoard = 9;
    internal const int FavoritesBoard = 10;
    internal const int NominatedBoard = 11;
    /// <summary>Added to a leaderboard by "Show My Castles".</summary>
    internal const int MyCastlesFlag = 0x80;
    /// <summary>The rank the client drops from the favorites board: the house is no longer on the Tour.</summary>
    internal const ushort OffTourRank = 0xFFFF;

    internal static readonly string[] BadgeNames = ["CastleRating-001", "CastleRating-002", "CastleRating-003", "CastleRating-004"];

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object s_lock = new();
    private static CastleTourFile s_file;
    private static DateTime s_written = DateTime.MinValue;

    internal static CastleTourSettings Settings {
        get {
            lock (s_lock) {
                return Load().Settings;
            }
        }
    }

    /// <summary>
    /// Whether a house of an owner is on the Tour, which opens it to everyone.
    /// </summary>
    /// <param name="ownerCharId">The owner.</param>
    /// <param name="templateId">The deed or structure template of the house.</param>
    internal static bool IsOnTour(ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            return Find(Load(), ownerCharId, templateId) is not null;
        }
    }

    /// <summary>
    /// Finds the house on the Tour of an owner, by its deed or structure template.
    /// </summary>
    /// <returns>A copy, or null when it is not on the Tour.</returns>
    internal static CastleTourHouse Get(ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            return Copy(Find(Load(), ownerCharId, templateId));
        }
    }

    /// <summary>
    /// The deed templates of a player's houses on the Tour.
    /// </summary>
    internal static List<uint> TemplatesOf(ulong ownerCharId) {
        lock (s_lock) {
            return [.. Live(Load()).Where(house => house.OwnerCharId == ownerCharId).Select(house => house.TemplateId)];
        }
    }

    /// <summary>
    /// A player's houses on the Tour, each with its place among all houses.
    /// </summary>
    internal static List<(CastleTourHouse House, int Rank)> MyCastles(ulong ownerCharId) {
        lock (s_lock) {
            var ranked = Ranked(Live(Load())).ToList();

            return [.. ranked.Select((house, index) => (house, index + 1))
                .Where(pair => pair.house.OwnerCharId == ownerCharId)
                .Select(pair => (Copy(pair.house), pair.Item2))];
        }
    }

    /// <summary>
    /// Puts a house on the Tour, or changes its type when it is on it already.
    /// </summary>
    internal static CastleTourResult TryAdd(Wizard owner, uint templateId, CastleTourType type) {
        if (!HouseTemplates.TryGet(templateId, out var template) || template.IsStructure || type is < CastleTourType.Decorated or > CastleTourType.Collection) {
            return CastleTourResult.Problem;
        }

        lock (s_lock) {
            var file = Load();
            var house = Find(file, owner.CharId, templateId);
            if (house is null) {
                house = new CastleTourHouse { OwnerCharId = owner.CharId, TemplateId = templateId, AddedUtc = DateTime.UtcNow };
                file.Houses.Add(house);
            }

            house.StructureTemplateId = template.HasStructure ? template.StructureTemplateId : 0;
            house.Type = type;
            house.OwnerName = owner.PlayerNameBehavior?.GetWizardName() ?? "";
            house.OwnerNameHex = owner.PlayerNameBehavior?.GetWizardNameAsByteHexString() ?? "";
            house.EndsUtc = DateTime.UtcNow.AddDays(Math.Max(1, file.Settings.Days));
            Save(file);
        }

        return CastleTourResult.Done;
    }

    /// <summary>
    /// Takes a house off the Tour.
    /// </summary>
    internal static CastleTourResult TryRemove(ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            var file = Load();
            var house = Find(file, ownerCharId, templateId);
            if (house is null) {
                return CastleTourResult.Problem;
            }

            file.Houses.Remove(house);
            Save(file);
        }

        return CastleTourResult.Done;
    }

    /// <summary>
    /// Picks a house for the Visit buttons: one added in the last day (New Today), or one of a type (Random).
    /// </summary>
    /// <param name="visitor">The player who visits; their own houses are left out.</param>
    /// <param name="newToday">Whether only houses added in the last 24 hours count.</param>
    /// <param name="type">The type, or <see cref="CastleTourType.Any"/>.</param>
    /// <param name="avoid">Houses ("owner:template") the player saw lately, picked only when nothing else is left.</param>
    internal static CastleTourHouse PickVisit(Wizard visitor, bool newToday, CastleTourType type, IReadOnlyCollection<string> avoid) {
        lock (s_lock) {
            var since = DateTime.UtcNow.AddDays(-1);
            var choices = Live(Load())
                .Where(house => house.OwnerCharId != visitor.CharId)
                .Where(house => !newToday || house.AddedUtc >= since)
                .Where(house => newToday || type == CastleTourType.Any || house.Type == type)
                .ToList();
            var fresh = choices.Where(house => !avoid.Contains(KeyOf(house))).ToList();
            var pool = fresh.Count > 0 ? fresh : choices;

            return pool.Count == 0 ? null : Copy(pool[Random.Shared.Next(pool.Count)]);
        }
    }

    /// <summary>
    /// The houses of a leaderboard, best first.
    /// </summary>
    /// <param name="viewer">The player who looks.</param>
    /// <param name="board">The leaderboard, with <see cref="MyCastlesFlag"/> for only the viewer's houses.</param>
    /// <returns>Copies, each with its rank; a favorite that left the Tour has <see cref="OffTourRank"/>.</returns>
    internal static List<(CastleTourHouse House, ushort Rank)> Leaderboard(Wizard viewer, int board) {
        var mine = (board & MyCastlesFlag) != 0;
        var type = board & ~MyCastlesFlag;
        lock (s_lock) {
            var file = Load();
            var live = Live(file).ToList();
            IEnumerable<CastleTourHouse> houses = type switch {
                < TopRatedBoard => live.Where(house => (int) house.Type == type),
                TopRatedBoard => live,
                FriendsBoard => live.Where(house => house.OwnerCharId != viewer.CharId && BuddyRelationshipCollection.AreFriends(house.OwnerCharId, viewer.CharId)),
                _ => [],
            };

            if (type == FavoritesBoard) {
                var favorites = PlayerOf(file, viewer.CharId, create: false)?.Favorites ?? [];
                var ranked = Ranked(live).ToList();

                return [.. favorites.Select(favorite => {
                    var house = Find(file, favorite.OwnerCharId, favorite.TemplateId);
                    if (house is not null) {
                        return (Copy(house), (ushort) Math.Min(ranked.IndexOf(house) + 1, OffTourRank - 1));
                    }

                    // Off the Tour: the client drops it from the list.
                    return (new CastleTourHouse { OwnerCharId = favorite.OwnerCharId, TemplateId = favorite.TemplateId }, OffTourRank);
                })];
            }

            return [.. Ranked(houses)
                .Select((house, index) => (house, (ushort) Math.Min(index + 1, OffTourRank - 1)))
                .Where(pair => !mine || pair.house.OwnerCharId == viewer.CharId)
                .Select(pair => (Copy(pair.house), pair.Item2))];
        }
    }

    /// <summary>
    /// Counts a visit.
    /// </summary>
    internal static void CountVisit(ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            var file = Load();
            if (Find(file, ownerCharId, templateId) is { } house) {
                house.Visits++;
                Save(file);
            }
        }
    }

    /// <summary>
    /// Stores a rating and gives the rater badge credit for it.
    /// </summary>
    /// <param name="rater">The player who rated.</param>
    /// <param name="ownerCharId">The owner of the house.</param>
    /// <param name="templateId">The deed template of the house.</param>
    /// <param name="rating">1 to 4 stars.</param>
    /// <param name="creditToday">The rater's credit today after this rating.</param>
    /// <param name="earned">The badges this rating earned.</param>
    /// <returns><c>false</c> when the house is not on the Tour or the rating is not 1 to 4.</returns>
    internal static bool TryRate(Wizard rater, ulong ownerCharId, uint templateId, int rating, out int creditToday, out List<string> earned) {
        creditToday = 0;
        earned = [];
        if (rating is < 1 or > 4) {
            return false;
        }

        lock (s_lock) {
            var file = Load();
            var house = Find(file, ownerCharId, templateId);
            if (house is null) {
                return false;
            }

            house.Ratings[rater.CharId.ToString(CultureInfo.InvariantCulture)] = rating;

            var player = PlayerOf(file, rater.CharId, create: true);
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (player.CreditDay != today) {
                player.CreditDay = today;
                player.CreditToday = 0;
                player.CreditedToday = [];
            }

            var key = KeyOf(house);
            if (player.CreditToday < Math.Max(0, file.Settings.DailyBadgeCredit) && !player.CreditedToday.Contains(key)) {
                var before = player.Credit;
                player.CreditToday++;
                player.CreditedToday.Add(key);
                player.Credit++;
                for (var tier = 0; tier < BadgeNames.Length && tier < file.Settings.BadgeCredit.Count; tier++) {
                    if (before < file.Settings.BadgeCredit[tier] && player.Credit >= file.Settings.BadgeCredit[tier]) {
                        earned.Add(BadgeNames[tier]);
                    }
                }
            }

            creditToday = player.CreditToday;
            Save(file);
        }

        return true;
    }

    /// <summary>
    /// The rating a player gave a house, or 0.
    /// </summary>
    internal static int RatingBy(ulong raterCharId, CastleTourHouse house)
        => house?.Ratings.GetValueOrDefault(raterCharId.ToString(CultureInfo.InvariantCulture)) ?? 0;

    internal static CastleTourFavoriteResult AddFavorite(ulong playerCharId, ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            var file = Load();
            var house = Find(file, ownerCharId, templateId);
            if (house is null || ownerCharId == playerCharId) {
                return CastleTourFavoriteResult.Problem;
            }

            var player = PlayerOf(file, playerCharId, create: true);
            if (player.Favorites.Any(favorite => favorite.OwnerCharId == ownerCharId && favorite.TemplateId == house.TemplateId)) {
                return CastleTourFavoriteResult.Added;
            }

            if (player.Favorites.Count >= Math.Max(1, file.Settings.MaxFavorites)) {
                return CastleTourFavoriteResult.NoSpace;
            }

            player.Favorites.Add(new CastleTourFavorite { OwnerCharId = ownerCharId, TemplateId = house.TemplateId });
            Save(file);

            return CastleTourFavoriteResult.Added;
        }
    }

    internal static bool RemoveFavorite(ulong playerCharId, ulong ownerCharId, uint templateId) {
        lock (s_lock) {
            var file = Load();
            var player = PlayerOf(file, playerCharId, create: false);
            var removed = player?.Favorites.RemoveAll(favorite => favorite.OwnerCharId == ownerCharId && favorite.TemplateId == templateId) ?? 0;
            if (removed > 0) {
                Save(file);
            }

            return removed > 0;
        }
    }

    internal static string KeyOf(CastleTourHouse house)
        => string.Create(CultureInfo.InvariantCulture, $"{house.OwnerCharId}:{house.TemplateId}");

    /// <summary>
    /// Best first: the highest average rating, then the most ratings, the most visits, and the oldest on the Tour.
    /// </summary>
    private static IEnumerable<CastleTourHouse> Ranked(IEnumerable<CastleTourHouse> houses)
        => houses.OrderByDescending(house => house.Average)
                 .ThenByDescending(house => house.Ratings.Count)
                 .ThenByDescending(house => house.Visits)
                 .ThenBy(house => house.AddedUtc);

    // Under s_lock.
    private static IEnumerable<CastleTourHouse> Live(CastleTourFile file) {
        var now = DateTime.UtcNow;

        return file.Houses.Where(house => house.EndsUtc > now);
    }

    // Under s_lock.
    private static CastleTourHouse Find(CastleTourFile file, ulong ownerCharId, uint templateId)
        => Live(file).FirstOrDefault(house => house.Names(ownerCharId, templateId));

    // Under s_lock.
    private static CastleTourPlayer PlayerOf(CastleTourFile file, ulong charId, bool create) {
        var key = charId.ToString(CultureInfo.InvariantCulture);
        if (!file.Players.TryGetValue(key, out var player) && create) {
            player = new CastleTourPlayer();
            file.Players[key] = player;
        }

        if (player is not null) {
            player.Favorites ??= [];
            player.CreditedToday ??= [];
        }

        return player;
    }

    private static CastleTourHouse Copy(CastleTourHouse house)
        => house is null ? null : new CastleTourHouse {
            OwnerCharId = house.OwnerCharId,
            OwnerName = house.OwnerName,
            OwnerNameHex = house.OwnerNameHex,
            TemplateId = house.TemplateId,
            StructureTemplateId = house.StructureTemplateId,
            Type = house.Type,
            AddedUtc = house.AddedUtc,
            EndsUtc = house.EndsUtc,
            Visits = house.Visits,
            Ratings = new(house.Ratings),
        };

    // Under s_lock. Read again when the file changed outside the server.
    private static CastleTourFile Load() {
        try {
            var written = File.Exists(FILE) ? File.GetLastWriteTimeUtc(FILE) : DateTime.MinValue;
            if (s_file is null || written != s_written) {
                s_file = written == DateTime.MinValue
                    ? new()
                    : JsonSerializer.Deserialize<CastleTourFile>(File.ReadAllText(FILE), s_options) ?? new();
                s_file.Settings ??= new();
                s_file.Settings.BadgeCredit ??= [];
                s_file.Houses ??= [];
                s_file.Houses.RemoveAll(house => house is null);
                foreach (var house in s_file.Houses) {
                    house.Ratings ??= [];
                }

                s_file.Players ??= [];
                s_written = written;
                Logger.Information("Castle Tours | {0}: {1} houses on the Tour.", Logger.Args(
                    written == DateTime.MinValue ? $"No {FILE} yet" : $"Read {FILE}", s_file.Houses.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Error("Castle Tours | Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
            s_file ??= new();
        }

        return s_file;
    }

    // Under s_lock.
    private static void Save(CastleTourFile file) {
        try {
            // Houses whose time ran out leave the file.
            var now = DateTime.UtcNow;
            file.Houses.RemoveAll(house => house.EndsUtc <= now);
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, s_options));
            File.Move(temp, FILE, overwrite: true);
            s_written = File.GetLastWriteTimeUtc(FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Error("Castle Tours | Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
