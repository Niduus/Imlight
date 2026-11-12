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
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>The character ids of Bot Players wizards: a range of their own, far above any real character's.</summary>
public static class BotIds {

    internal const ulong BASE = 0x7E00_0000_0000_0000UL;
    private const ulong RANGE = 0x0000_0000_FFFF_FFFFUL;

    public static ulong CharIdOf(int botId) => BASE + (ulong) botId;

    public static bool IsBot(ulong charId) => charId >= BASE && charId <= BASE + RANGE;

    /// <summary>For a wizard's object id (its character id + 2, Wizard.GetGameObjectId).</summary>
    public static bool IsBotObject(ulong globalId) => Wizard.TryGetCharacterId(globalId, out var charId) && IsBot(charId);

}

// What the bots' bodies (BotBody) and the battles (CombatDuelComponent) tell each other.

/// <summary>A bot's body asks a battle for a circle on the players' side.</summary>
internal sealed record BotJoinBattle(CoreObject BotObject, IActorRef BotActor, string Name);

/// <summary>The battle seated the bot: where it stands now.</summary>
internal sealed record BotSeated(Imcodec.Math.Vector3 Where, float Yaw);

/// <summary>The battle would not take the bot.</summary>
internal sealed record BotNotSeated(string Why);

/// <summary>A player kicks bots out of their battle: one (its object id) or every bot (0). No bot joins it again.</summary>
internal sealed record BotKickFromBattle(ulong BotGlobalId, string By);

/// <summary>Tells a body to show itself to a player who just came into its area.</summary>
internal sealed record BotShowTo(IActorRef Player);

/// <summary>Tells a body to join the battle of a player, after a wait.</summary>
internal sealed record BotGoJoin(IActorRef Battle, TimeSpan Wait, string For);

/// <summary>Tells a body to leave the world.</summary>
internal sealed record BotLeave(string Why);

/// <summary>
/// The bots out in the world: their bodies (BotBody actors), by the brain's bot number. Every real player who comes into an
/// area is shown the bots there (ZonePlayerSupervisor).
/// </summary>
internal static class BotBodies {

    internal sealed record Out(int Id, string Name, IActorRef Body, IActorRef Zone, string ZonePath, ulong GlobalId);

    private static readonly ConcurrentDictionary<int, Out> s_out = new();
    private static ActorSystem s_system;

    // Wizards of a school, for a bot's school (the seven of the character maker).
    private static readonly MagicSchool[] s_schools = [
        MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Storm, MagicSchool.Myth, MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance,
    ];

    internal static int Count => s_out.Count;

    internal static void Start(ActorSystem system) {
        s_system = system;
        system.Scheduler.Advanced.ScheduleRepeatedly(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(BotPopulation.TICK_SECONDS), Populate);
    }

    // ---------------------------------------------------------------------
    // Filling the areas real players are in (Niduus, 2026-11-12: "Begin to populate")
    // ---------------------------------------------------------------------

    private static int s_populating;

    // Every few seconds: each area with real players in it gets bots until it has its share, one at a time, so they
    // come in one by one as players would; an area with too many (players left) lets one go.
    private static void Populate() {
        if (!BotWire.Enabled || !BotLink.Ready || !BotPopulation.On || s_system is null
            || System.Threading.Interlocked.Exchange(ref s_populating, 1) == 1) {
            return;
        }

        try {
            foreach (var (zone, players) in s_players) {
                if (players.IsEmpty || !s_zones.TryGetValue(zone, out var info) || !BotPopulation.Allowed(info.Path, info.Instance)) {
                    continue;
                }

                var wanted = BotPopulation.Wanted(info.Path, players.Values.Select(v => (int) v));
                var here = InZone(zone);
                if (here.Count < wanted) {
                    var someone = players.Keys.ElementAt(Random.Shared.Next(players.Count));
                    SpawnFor(zone, info.Path, someone);
                }
                else if (here.Count > wanted + 1) {
                    here[Random.Shared.Next(here.Count)].Body.Tell(new BotLeave("its area has enough bots for the players there"));
                }
            }
        }
        catch (Exception e) {
            Logger.Warning("Bot Players: filling the areas failed: {0}", Logger.Args(e.Message));
        }
        finally {
            System.Threading.Interlocked.Exchange(ref s_populating, 0);
        }
    }

    // A bot for an area, made after one of its players (their level and where they are), somewhere in the area.
    private static void SpawnFor(IActorRef zone, string zonePath, IActorRef player) {
        player.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(
                new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(), TimeSpan.FromSeconds(3))
            .ContinueWith(task => {
                if (task.IsCompletedSuccessfully && task.Result?.Wizard is { } wizard && wizard.Zone == zonePath) {
                    // A level that fits the world, not the player's own when the player is far above it.
                    var level = BotPopulation.LevelFor(zonePath, (int) wizard.MagicSchoolBehavior.Level, Random.Shared);
                    Spawn(wizard, zone, level, BotPopulation.SpotIn(zonePath, wizard.Location), out _);
                }
            });
    }

    internal static IReadOnlyList<Out> All => [.. s_out.Values.OrderBy(o => o.Id)];

    internal static IReadOnlyList<Out> InZone(IActorRef zone) => [.. s_out.Values.Where(o => o.Zone.Equals(zone)).OrderBy(o => o.Id)];

    // The real players in each area (by the area's actor), so bots are only where real players are.
    private static readonly ConcurrentDictionary<IActorRef, ConcurrentDictionary<IActorRef, byte>> s_players = new();

    /// <summary>How many real players are in an area.</summary>
    internal static int PlayersIn(IActorRef zone) => s_players.TryGetValue(zone, out var players) ? players.Count : 0;

    internal static void PlayerLeft(IActorRef zone, IActorRef player) {
        if (s_players.TryGetValue(zone, out var players)) {
            players.TryRemove(player, out _);
        }
    }

    // What each area is (its path, and whether it is a copy for one group: a dungeon or other instance).
    private static readonly ConcurrentDictionary<IActorRef, (string Path, bool Instance)> s_zones = new();

    /// <summary>A player came into an area: every bot there shows itself to them.</summary>
    internal static void PlayerEntered(IActorRef zone, IActorRef player, string zonePath, bool isInstance) {
        s_zones[zone] = (zonePath ?? "", isInstance);
        // How many bots this player brings to the area: 3 to 6 (Niduus, 2026-11-12).
        s_players.GetOrAdd(zone, _ => new ConcurrentDictionary<IActorRef, byte>())[player] =
            (byte) Random.Shared.Next(BotPopulation.MinPerPlayer, BotPopulation.MaxPerPlayer + 1);
        if (s_out.IsEmpty) {
            return;
        }

        foreach (var bot in s_out.Values.Where(o => o.Zone.Equals(zone))) {
            bot.Body.Tell(new BotShowTo(player));
        }
    }

    internal static void Gone(int id) => s_out.TryRemove(id, out _);

    /// <summary>Every bot leaves (the switch was turned off, or the mini server stopped).</summary>
    internal static void RemoveAll(string why) {
        foreach (var bot in s_out.Values) {
            bot.Body.Tell(new BotLeave(why));
        }
    }

    /// <summary>
    /// Brings a test bot out next to a player: the brain picks who it is (a bot that comes back, or a new one), and its
    /// body walks a loop near where it came in. Returns what to tell the player.
    /// </summary>
    internal static string SpawnNear(Wizard player, IActorRef playerSession, IActorRef zone) {
        // A few steps to the side of the player.
        var at = player.Location;
        var side = player.Orientation.Z + (MathF.PI / 2);
        var near = new Imcodec.Math.Vector3(at.X + (MathF.Cos(side) * 90), at.Y + (MathF.Sin(side) * 90), at.Z);
        Spawn(player, zone, BotPopulation.LevelFor(player.Zone, (int) player.MagicSchoolBehavior.Level, Random.Shared), near, out var said);

        return said;
    }

    /// <summary>
    /// Brings a bot out in an area at a place, made after a player there (their look as a base, their area); the brain
    /// picks who it is. Returns whether it came out; said is what to tell a player.
    /// </summary>
    private static bool Spawn(Wizard player, IActorRef zone, int wantedLevel, Imcodec.Math.Vector3 where, out string said) {
        said = Precheck(zone);
        if (said is not null) {
            return false;
        }

        said = SpawnNow(player, zone, wantedLevel, where);

        return s_lastSpawnOk;
    }

    [ThreadStatic] private static bool s_lastSpawnOk;

    private static string Precheck(IActorRef zone) {
        if (!BotWire.Enabled) {
            return "Bot Players is off. Turn it on in Ahziael (Game Rules > Battles and World > Bot Players), wait about 10 seconds, then try again.";
        }

        if (!BotLink.Ready) {
            return "The Bot Players mini server is still starting. Try again in a few seconds.";
        }

        if (s_system is null || zone is null) {
            return "Bots can't come out here.";
        }

        return null;
    }

    private static string SpawnNow(Wizard player, IActorRef zone, int wantedLevel, Imcodec.Math.Vector3 where) {
        s_lastSpawnOk = false;

        // Its own gender and look, not a copy of the player's (Niduus, 2026-11-12: "random looks").
        var gender = Random.Shared.Next(2) == 0 ? eGender.Male : eGender.Female;
        var (firsts, middles, lasts) = WizardNameBank.WizardNameListSizes(gender);
        var school = s_schools[Random.Shared.Next(s_schools.Length)];
        var level = (byte) Math.Clamp(wantedLevel, 1, 200);
        var answer = BotLink.Ask(new BotRequest {
            Op = "make", Gender = gender.ToString(), Firsts = Math.Min(firsts, 256), Middles = Math.Min(middles, 256),
            Lasts = Math.Min(lasts, 256), School = school.ToString(), Level = level, Zone = player.Zone,
        });
        if (answer?.Bot is not { } who) {
            return "The Bot Players mini server did not answer. Its log is in Ahziael (Logs > Bot Players log).";
        }

        if (s_out.ContainsKey(who.Id)) {
            return "That bot is already out.";
        }

        if (!Enum.TryParse<MagicSchool>(who.School, out var botSchool)) {
            botSchool = school;
        }

        Wizard bot;
        try {
            bot = Wizard.NewBot(BotIds.CharIdOf(who.Id), botSchool, BotLooks.NewAvatar(player.WizardAvatar, gender, new Random(who.Id)), who.NameKeys,
                (byte) Math.Clamp(who.Level, 1, 200));
            // Its spells: what a wizard of its school has at its level. A third of the bots took Life as a second school.
            bot.SpellbookBehavior.SpellList = BotDeck.For(botSchool, Math.Clamp(who.Level, 1, 200), who.Id % 3 == 0);

            // The title of a badge a wizard of its level has most likely earned (Niduus, 2026-11-12: "no badge on test wizard").
            if (BotLooks.Badge(who.Level, new Random(who.Id * 7919)) is var (badgeId, title)) {
                bot.BadgeBehavior.SelectedBadgeId = badgeId;
                bot.PlayerNameBehavior.BadgeTitle = title;
            }
        }
        catch (Exception e) {
            Logger.Error("Bot Players: could not make bot #{0}: {1}", Logger.Args(who.Id, e));
            BotLink.Note(who.Id, "?", "gone", "could not be made: " + e.Message);

            return "The bot could not be made. The server log says why.";
        }

        bot.Location = where;
        bot.Orientation = new Imcodec.Math.Vector3(0, 0, Random.Shared.NextSingle() * MathF.Tau);
        bot.Zone = player.Zone;
        bot.ZoneDisplayName = player.ZoneDisplayName;

        var name = bot.PlayerNameBehavior.GetWizardName();
        var info = new BotBody.Info(who.Id, name, bot, zone, player.Zone, ActorRefs.Nobody);
        IActorRef body;
        try {
            body = s_system.ActorOf(Props.Create(() => new BotBody(info)), $"bot-{who.Id}-{Guid.NewGuid():N}");
        }
        catch (Exception e) {
            Logger.Error("Bot Players: could not start the body of bot #{0}: {1}", Logger.Args(who.Id, e));
            BotLink.Note(who.Id, name, "gone", "its body could not start");

            return "The bot could not come out. The server log says why.";
        }

        s_out[who.Id] = new Out(who.Id, name, body, zone, player.Zone, bot.GameObjectID);
        s_lastSpawnOk = true;
        BotLink.Note(who.Id, name, "spawned", $"{(who.Returning ? "came back" : "new")}, {botSchool} level {who.Level}, " +
                                               $"{bot.SpellbookBehavior.SpellList.Count} spells in its deck, " +
                                               $"made after {player.PlayerNameBehavior.GetWizardName()} in {player.Zone}");

        return $"{name} ({botSchool}, level {who.Level}) came out next to you{(who.Returning ? ", back for the " + who.Times + (who.Times == 2 ? "nd" : who.Times == 3 ? "rd" : "th") + " time" : "")}.";
    }

}
