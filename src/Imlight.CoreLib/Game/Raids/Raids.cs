using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Raids;

/*
 * Guild raids (Niduus, 2026-10-25: "Build guild raids, starting with ONE raid end to end"). In the live game a guild
 * opens a raid at one of the three raid gates on the battle island of its guild grounds (RaidGate1-3, which its
 * RaidUnlockTrigger1-3 turn into): a wizard uses a raid key (RaidKeyBehavior), the gate opens a portal
 * (RaidTeleportBehavior: the raid's zone, 12 wizards, open for 3 hours), and walking into it goes into the raid. The
 * raid itself is the zone's own trigger script (triggers.xml): pads that need a number of wizards on them, a 30 minute
 * timer (ResBGAdjustTime), bosses, coin puzzles, teleport pads, and the end of the raid when the last boss falls or
 * the time runs out.
 *
 * Here:
 * - the gates stand open on the battle island (spiraldb-extra/ZoneObjects/Housing_Guild-GuildStarter_Ext.json);
 *   pressing X on one lists the raids and opens a portal there (RaidGateComponent). [Raids] NeedKey asks for a raid
 *   key in the backpack and uses it up.
 * - walking into the open gate goes into the raid copy of the wizard who opened it, so everyone who walks in plays
 *   the same raid; only the first one in starts it fresh.
 * - the raid zone runs its own script with the server's trigger engine. The steps the engine did not know are added
 *   in Game/Results/Handlers/ResRaidHandlers.cs (timer, win, end, badges, teleport pads, sigil spells).
 * - a group smaller than the raid is made for can still do it ([Raids] SmallGroups): the pads that count wizards ask
 *   for no more wizards than are in the raid, and a pad keeps a wizard who stepped on it (ZoneScriptState).
 * - when the raid ends everyone goes back to the battle island after [Raids] ReturnSeconds.
 * - `.raid` shows the raid's state and can leave it or push a stuck step (CommandRaidProtocol).
 */
internal static class Raids {

    internal static bool Enabled => ConfigurationManager.GetValue("Raids.Enabled", true);
    internal static bool NeedKey => ConfigurationManager.GetValue("Raids.NeedKey", false);
    internal static bool SmallGroups => ConfigurationManager.GetValue("Raids.SmallGroups", true);
    internal static int ReturnSeconds => Math.Max(5, ConfigurationManager.GetValue("Raids.ReturnSeconds", 30));
    internal static int PortalMinutes => Math.Max(1, ConfigurationManager.GetValue("Raids.PortalMinutes", 180));

    internal const string ZONE_PREFIX = "Raids/";
    internal const string START_LOCATION = "Start";
    // Where a wizard who comes back from a raid lands: the battle island's arrival spot (Housing_Guild/GuildStarter_Ext;
    // "Target location-FromRaid" is the main island's end of the island teleporters).
    internal const string RETURN_LOCATION = "Target location-ToRaid";
    // The event the raid scripts end the raid on when the time runs out ("Trigger MATCH_END").
    internal const string TIME_UP_EVENT = "EVENT_Battleground_EndBattle";

    /// <summary>
    /// A raid: its name, zone, how many wizards it is made for, the keys that open it, and where its teleport pads
    /// go (trigger name to zone location: the pads' ResTeleport names no destination).
    /// </summary>
    internal sealed record Raid(string Name, string Zone, string KeyNameKey, int Players, uint[] Keys,
                                IReadOnlyDictionary<string, string> Teleports);

    private static Dictionary<string, string> Pads(params (string Trigger, string Location)[] pads)
        => pads.ToDictionary(p => p.Trigger, p => p.Location, StringComparer.OrdinalIgnoreCase);

    // The teleport pads were matched by where each pad's volume and each location stand in the zone (volumes.xml and
    // gamedata of Raid_DS_Knowledge): a pad goes to the location at its other end.
    internal static readonly IReadOnlyList<Raid> All = [
        new("The Voracious Void", "Raids/Raid_DS_Knowledge", "Items_00024825", 12, [1536117, 1659453], Pads(
            ("Trigger_Teleport_2_Street1", "Target_Teleport_From_Street2"),
            ("Trigger_Teleport_2_Street2_From1", "Target_Street2_FromStreet1"),
            ("Trigger_Teleport_2_Street3_From2", "Target_Street3_From2"),
            ("Trigger_Teleport_2_Street2_From3", "Target_Street2_From3"),
            ("Trigger_Teleport_SinglePlayer_BossFight", "Target_FromStreet3_toBossFight"),
            ("Trigger_Teleport_SinglePlayer_FromBossFight", "Target_From_FinalBoss"),
            ("Trigger_Spiral_Tele_Top", "Target_Spiral_Tele_Bottom"),
            ("Trigger_Spiral_Tele_Bottom", "Target_Spiral_Tele_Top"),
            ("Trigger_Teleport_2_Eye", "Target_Teleport_2_Eye"),
            ("Trigger_Teleport_From_Eye", "Target_Teleport_From_Eye"),
            ("Trigger_Teleport_2_Moon", "Target_Teleport_2_Moon"),
            ("Trigger_Teleport_From_Moon", "Target_Teleport_From_Moon"),
            ("Trigger_Teleport_2_Sun", "Target_2_Sun"),
            ("Trigger_Teleport_From_Sun", "Target_From_Sun"),
            ("Trigger_Teleport_2_Star", "Target_Teleport_2_Star"),
            ("Trigger__Teleport_From_Star", "Target_Teleport_From_Star"))),
    ];

    private static int s_privateZoneTestRegistered;

    /// <summary>
    /// Makes a raid zone always load as a private copy, so a wizard who logs back in inside one lands in the raid's
    /// copy again. Called once at startup.
    /// </summary>
    internal static void Preload() {
        if (Interlocked.Exchange(ref s_privateZoneTestRegistered, 1) == 0) {
            ZoneInstanceOwners.RegisterPrivateZoneTest(IsRaidZone);
        }
    }

    internal static bool IsRaidZone(string zonePath)
        => zonePath is not null && zonePath.StartsWith(ZONE_PREFIX, StringComparison.OrdinalIgnoreCase);

    internal static Raid For(string zonePath)
        => zonePath is null ? null : All.FirstOrDefault(r => string.Equals(r.Zone, zonePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds a raid by a word of its name or its zone ("void", "DS_Knowledge").
    /// </summary>
    internal static Raid Find(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        text = text.Trim();

        return All.FirstOrDefault(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.Zone.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The location a raid's teleport pad sends a wizard to, or null when the pad is not known.
    /// </summary>
    internal static string TeleportTarget(string zonePath, string triggerName)
        => triggerName is not null && For(zonePath)?.Teleports.TryGetValue(triggerName, out var location) == true
            ? location
            : null;

    /// <summary>
    /// The counters a raid's pads keep the number of wizards on: a pad's Enter_ trigger adds to it and its Exit_
    /// trigger takes from it. A counter the raid ends on (its cheat checks: "Cheaters never prosper") is left out, so
    /// a small group is not thrown out of the raid by it.
    /// </summary>
    internal static HashSet<string> PresenceCounters(WizZoneTriggers triggers) {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var trigger in triggers?.m_triggers ?? []) {
            if (trigger is null) {
                continue;
            }

            var events = trigger.m_fireEvents?.Select(e => e.ToString()).ToList() ?? [];
            var results = trigger.m_results?.m_results?.Where(r => r is not null).ToList() ?? [];
            foreach (var counter in results.OfType<ResZoneCounter>()) {
                if (string.Equals(counter.m_action, "ZCA_Set", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (counter.m_value > 0 && events.Any(e => e.StartsWith("Enter_", StringComparison.OrdinalIgnoreCase))) {
                    added.Add(counter.m_counterName);
                }
                else if (counter.m_value < 0 && events.Any(e => e.StartsWith("Exit_", StringComparison.OrdinalIgnoreCase))) {
                    taken.Add(counter.m_counterName);
                }
            }

            if (results.Any(r => r is ResUnknown117B3E95)) {
                foreach (var requirement in trigger.m_requirements?.m_requirements?.OfType<ReqZoneCounter>() ?? []) {
                    ending.Add(requirement.m_counterName);
                }
            }
        }

        added.IntersectWith(taken);
        added.ExceptWith(ending);
        added.RemoveWhere(name => name.Contains("Cheat", StringComparison.OrdinalIgnoreCase));

        return added;
    }

    /// <summary>
    /// The transfer back out of a raid: the battle island of the wizard's guild grounds (or wherever the Guild Museum
    /// sends a wizard without grounds).
    /// </summary>
    internal static ZONE_102_PROTOCOL.MSG_ZONETRANSFER ReturnTransfer(Wizard wizard) {
        var transfer = GuildMuseum.GuildMuseum.ReturnTransfer(wizard);
        if (string.Equals(transfer.DestinationZone, GuildMuseum.GuildMuseum.GUILD_GROUNDS_ZONE, StringComparison.OrdinalIgnoreCase)) {
            transfer.DestinationLocation = RETURN_LOCATION;
        }

        return transfer;
    }

    #region Portals

    /// <summary>
    /// An open portal: who opened it (whose raid copy everyone goes into), until when, and who went in.
    /// </summary>
    internal sealed class Portal(Raid raid, ulong ownerCharId, DateTime until) {

        public Raid Raid { get; } = raid;
        public ulong OwnerCharId { get; } = ownerCharId;
        public DateTime Until { get; } = until;
        public ConcurrentDictionary<ulong, byte> Entered { get; } = new();
        public bool Ended { get; set; }

        private int _started;

        /// <summary>
        /// True for the first wizard in, who starts the raid fresh.
        /// </summary>
        public bool TryStart() => Interlocked.Exchange(ref _started, 1) == 0;

        public bool IsOpen => !Ended && DateTime.UtcNow < Until;

    }

    // One open portal per wizard who opened one.
    private static readonly ConcurrentDictionary<ulong, Portal> s_portals = new();

    internal static Portal Open(Raid raid, ulong ownerCharId) {
        var portal = new Portal(raid, ownerCharId, DateTime.UtcNow.AddMinutes(PortalMinutes));
        s_portals[ownerCharId] = portal;

        return portal;
    }

    /// <summary>
    /// The portal a wizard went into, if one is still open.
    /// </summary>
    internal static Portal PortalOf(ulong charId)
        => s_portals.Values.FirstOrDefault(p => p.IsOpen && (p.OwnerCharId == charId || p.Entered.ContainsKey(charId)));

    /// <summary>
    /// Closes the portals of a raid that has ended, so nobody walks back into the finished raid.
    /// </summary>
    internal static void EndRun(string zonePath, IEnumerable<ulong> charIds) {
        var ids = charIds.ToHashSet();
        foreach (var portal in s_portals.Values) {
            if (string.Equals(portal.Raid.Zone, zonePath, StringComparison.OrdinalIgnoreCase)
                && (ids.Contains(portal.OwnerCharId) || portal.Entered.Keys.Any(ids.Contains))) {
                portal.Ended = true;
            }
        }
    }

    #endregion

    #region Sigil spells

    /// <summary>
    /// Spells a raid's script casts into the battles going on in it (ResCastSigilSpell, "cast on Vanguard"): the
    /// players who were in the raid then, and until when a battle of theirs takes them at the start of its next round.
    /// </summary>
    internal sealed record SigilCast(long Id, string ZonePath, HashSet<ulong> CharIds, string[] Spells, DateTime Until);

    private static readonly Lock s_sigilLock = new();
    private static readonly List<SigilCast> s_sigilCasts = [];
    private static long s_sigilId;
    // A battle's round can take a while to come round (cinematics), so a cast waits this long for it.
    private static readonly TimeSpan SigilCastWindow = TimeSpan.FromSeconds(90);

    internal static void CastSigilSpells(string zonePath, IEnumerable<ulong> charIds, string[] spells) {
        lock (s_sigilLock) {
            s_sigilCasts.RemoveAll(c => c.Until < DateTime.UtcNow);
            s_sigilCasts.Add(new SigilCast(Interlocked.Increment(ref s_sigilId), zonePath, charIds.ToHashSet(), spells,
                DateTime.UtcNow + SigilCastWindow));
        }
    }

    /// <summary>
    /// The sigil spells waiting for a battle with these players that it has not had yet.
    /// </summary>
    internal static List<SigilCast> SigilCastsFor(IEnumerable<ulong> charIds, ICollection<long> alreadyCast) {
        var ids = charIds.ToHashSet();
        if (ids.Count == 0) {
            return [];
        }

        lock (s_sigilLock) {
            return s_sigilCasts.Where(c => c.Until >= DateTime.UtcNow && !alreadyCast.Contains(c.Id) && c.CharIds.Overlaps(ids)).ToList();
        }
    }

    #endregion

}
