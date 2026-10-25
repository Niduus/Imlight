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
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>One line the server announces: its key in announcements.json, where the Manager lists it, and its words.</summary>
/// <param name="Fields">The {words} the server fills in, each with what it becomes.</param>
internal sealed record AnnouncementLine(string Key, string Group, string Name, string When, string Default,
    (string Field, string Means)[] Fields);

/// <summary>
/// Every line the server announces (Niduus, 2026-10-25: "In the manager, if anything causes an announcement, I want it
/// all under an Announcement tab so I can adjust what it says"). The Manager's Announcements tab lists these and writes
/// the changed words to announcements.json next to the server; the server reads that again within a second
/// (Announcements.cs), so a change shows the next time the line is sent. This file is shared by the server and the app.
/// Battle cheat announcements are not here: their words live in each cheat.
/// </summary>
internal static class AnnouncementCatalog {

    internal const string FILE = "announcements.json";

    private static readonly (string, string) TIME_LEFT = ("{time left}", "the time left, such as \" (2d 3h left)\", or nothing when it has no end");
    private static readonly (string, string) EVENT = ("{event}", "the event's name, such as Fire Deckathalon");

    internal static readonly AnnouncementLine[] All = [
        // Events (Server tab, Events card)
        new("event.on", "Events", "Event starts", "Everyone online, when an event starts. Also shown to each wizard when they log in during it.",
            "Event on: {event}{time left}. {how to join}", [EVENT, TIME_LEFT, ("{how to join}", "one of the two \"how to join\" lines below")]),
        new("event.join.deckathalon", "Events", "How to join: Deckathalon", "Put in place of {how to join} for a Deckathalon.",
            "Type .deckathalon to climb the tower.", []),
        new("event.join.other", "Events", "How to join: other events", "Put in place of {how to join} for every other event.",
            "Click the event button at the top of the screen to see it, or type .events.", []),
        new("event.over", "Events", "Event ends", "Everyone online, when an event ends (not a Deckathalon).",
            "Event over: {event}. Its rewards can still be claimed for 7 days.", [EVENT]),
        new("event.over.deckathalon", "Events", "Deckathalon ends", "Everyone online, when a Deckathalon ends.",
            "Event over: {event}.", [EVENT]),

        // Deckathalon climbs
        new("deckathalon.floor", "Deckathalon", "Floor cleared", "The climber, after winning a floor.",
            "Floor {floor} cleared ({points} points). Walk into the portal to go up to floor {next floor}: {next foe}.",
            [("{floor}", "the floor just won"), ("{points}", "the climb's points"), ("{next floor}", "the floor above"),
             ("{next foe}", "who waits there, such as Bloodbat, or the professor line below")]),
        new("deckathalon.professor", "Deckathalon", "Next foe: the professor", "Put in place of {next foe} when the floor above is the top.",
            "{professor} waits there", [("{professor}", "the school's professor")]),
        new("deckathalon.checkpoint", "Deckathalon", "Checkpoint saved", "The climber, every few floors.",
            "Checkpoint saved: next time you enter the tower, you can start from floor {floor}.", [("{floor}", "the floor they can start from")]),
        new("deckathalon.conquered", "Deckathalon", "Tower conquered", "The climber, after beating the top floor's professor.",
            "You beat {professor} and conquered the {school} Deckathalon with {points} points!",
            [("{professor}", "the school's professor"), ("{school}", "the tower's school"), ("{points}", "the climb's points")]),
        new("deckathalon.top", "Deckathalon", "Top reached", "The climber, at the top of a tower set shorter than 30 floors.",
            "You reached the top of the {school} Deckathalon (floor {floor}) with {points} points!",
            [("{school}", "the tower's school"), ("{floor}", "the top floor"), ("{points}", "the climb's points")]),
        new("deckathalon.back", "Deckathalon", "Way back", "After the top line.", "Walk into the portal to go back to the arena.", []),
        new("deckathalon.fell", "Deckathalon", "Climb lost", "The climber, after losing or fleeing a floor's battle.",
            "The Deckathalon is over for this climb: you fell on floor {floor} with {points} points.",
            [("{floor}", "the floor they fell on"), ("{points}", "the climb's points")]),
        new("deckathalon.best", "Deckathalon", "Best floor", "After the climb lost line.",
            "Your best: floor {best}. Type .deckathalon to climb again.", [("{best}", "their best floor")]),
        new("deckathalon.left", "Deckathalon", "Left the tower", "The climber, when they walk out of the tower.",
            "You left the {school} Deckathalon on floor {floor} with {points} points.",
            [("{school}", "the tower's school"), ("{floor}", "the floor they were on"), ("{points}", "the climb's points")]),

        // Loyalty Store
        new("loyalty.new", "Loyalty Store", "New wares", "Everyone online, when Majid's wares change (if Announce in game is on).",
            "Majid's Loyalty Store has new wares: {wares}{time left}. Find him in the Shopping District.",
            [("{wares}", "the name of the new set of wares"), TIME_LEFT]),

        // Member benefits
        new("benefit.on", "Member benefits", "Benefit turns on", "Everyone online, when a member benefit turns on.",
            "Member benefit on: {benefit}{time left}", [("{benefit}", "the benefit's name"), TIME_LEFT]),
        new("benefit.off", "Member benefits", "Benefit turns off", "Everyone online, when a member benefit turns off.",
            "Member benefit off: {benefit}", [("{benefit}", "the benefit's name")]),
        new("benefit.login", "Member benefits", "Login: heading", "Each wizard when they log in, above the benefits that are on.",
            "Member benefits on:", []),
        new("benefit.login.line", "Member benefits", "Login: each benefit", "One line per benefit that is on, under the heading.",
            "{benefit}{time left}", [("{benefit}", "the benefit's name"), TIME_LEFT]),

        // Crowns Rewards
        new("crowns.got", "Crowns Rewards", "Reward earned", "The buyer, when their crowns spent reach a reward.",
            "{event}: you spent {crowns} crowns and got {item}!",
            [("{event}", "the Crowns Rewards name"), ("{crowns}", "the reward's crowns"), ("{item}", "the reward, such as Mega Snack Pack x2")]),
        new("crowns.full", "Crowns Rewards", "Reward with no room", "The buyer, when a reward did not fit in their backpack.",
            "{event}: you reached the {crowns} crowns reward ({item}), but there was no room for it.",
            [("{event}", "the Crowns Rewards name"), ("{crowns}", "the reward's crowns"), ("{item}", "the reward")]),
        new("crowns.next", "Crowns Rewards", "Where you stand", "The buyer, after a purchase, and once per login when they open the Crown Shop.",
            "{event}: you spent {spent} crowns. Spend {more} more for {item}.{ends}",
            [("{event}", "the Crowns Rewards name"), ("{spent}", "crowns spent so far"), ("{more}", "crowns still to spend"),
             ("{item}", "the next reward"), ("{ends}", "\" Ends in 2d 3h.\", or nothing when it has no end")]),
        new("crowns.all", "Crowns Rewards", "Every reward earned", "Same as above, once every reward is earned.",
            "{event}: you spent {spent} crowns and got every reward.{ends}",
            [("{event}", "the Crowns Rewards name"), ("{spent}", "crowns spent so far"), ("{ends}", "\" Ends in 2d 3h.\", or nothing")]),

        // Guild raids
        new("raid.timer", "Guild raids", "Raid timer starts", "Everyone in the raid, when a timer of a minute or more starts.",
            "The raid timer has started: {time}.", [("{time}", "the timer, such as 30:00")]),
        new("raid.sigil", "Guild raids", "Sigil spell cast", "Everyone in the raid, when a sigil spell goes into its battles.",
            "A sigil spell is cast on the raid's battles!", []),
        new("raid.won", "Guild raids", "Raid won", "Everyone in the raid, when it is won.", "The raid is won! Well done.", []),
        new("raid.complete", "Guild raids", "Raid over (won)", "Everyone in the raid, when a won raid ends.",
            "The raid is complete! Everyone goes back to the guild grounds in {seconds} seconds.", [("{seconds}", "seconds until they go back")]),
        new("raid.over", "Guild raids", "Raid over (not won)", "Everyone in the raid, when it ends without a win.",
            "The raid is over. Everyone goes back to the guild grounds in {seconds} seconds.", [("{seconds}", "seconds until they go back")]),

        // Guilds
        new("guild.joined", "Guilds", "Member joined", "The guild's members.", "[Guild] {wizard} joined the guild!", [("{wizard}", "who joined")]),
        new("guild.left", "Guilds", "Member left", "The guild's members.", "[Guild] {wizard} left the guild.", [("{wizard}", "who left")]),
        new("guild.removed", "Guilds", "Member removed", "The guild's members.", "[Guild] {wizard} was removed from the guild.", [("{wizard}", "who was removed")]),
        new("guild.renamed", "Guilds", "Guild renamed", "The guild's members.", "[Guild] {wizard} renamed the guild to {guild}.",
            [("{wizard}", "who renamed it"), ("{guild}", "the new name")]),
        new("guild.leader", "Guilds", "New leader", "The guild's members.",
            "[Guild] {wizard} leads the guild now. The guild house is now {wizard}'s guild house.", [("{wizard}", "the new leader")]),
        new("guild.ended", "Guilds", "Guild ended", "The guild's members.", "[Guild] {wizard} ended the guild {guild}.",
            [("{wizard}", "who ended it"), ("{guild}", "the guild's name")]),

        // Your own announcements (Play tab, Announcement card)
        new("manager.box", "Your announcements", "Box on screen", "Everyone online, when you send an announcement with Box on screen. A cheat's \"announce\" block in a box uses it too.",
            "<center>Announcement</center>\n{text}", [("{text}", "what you typed")]),
        new("manager.chat", "Your announcements", "Chat line", "Everyone online, when you send an announcement with Chat line. A cheat's \"announce\" block as a chat line uses it too.",
            "Announcement: {text}", [("{text}", "what you typed")]),

        // Server notices
        new("server.kicked", "Server notices", "Kicked", "A player who is kicked from the server.", "You have been kicked from the server.", []),
        new("server.diedelete", "Server notices", "Die = Delete", "A player whose wizard died with Die = Delete on.",
            "{wizard} died in battle. Die = Delete is on, so this wizard is gone for good.", [("{wizard}", "the wizard's name")]),
    ];

    private static readonly Dictionary<string, AnnouncementLine> s_byKey = All.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);

    internal static AnnouncementLine? Find(string key) => s_byKey.GetValueOrDefault(key);

    /// <summary>The words with each {field} filled in; a field with no value stays as it is written.</summary>
    internal static string Fill(string text, IReadOnlyDictionary<string, string> values)
        => Regex.Replace(text ?? "", @"\{([^{}]+)\}",
            m => values.TryGetValue(m.Groups[1].Value.Trim(), out var value) ? value : m.Value);

}
