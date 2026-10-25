using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Results.Handlers;

/*
 * The steps of the raids' scripts (Game/Raids) the engine did not know. The raids are the live game's battlegrounds
 * against creatures, so most of these come from its battleground system: the match timer (ResBGAdjustTime), the win
 * (ResUnknown5ABB361D, on the last boss's death) and the end of the match (ResUnknown117B3E95), whose meaning shows in
 * where the scripts use them ("Trigger MATCH_END", "Trigger_FinalBoss_Death").
 */

internal static class RaidResult {

    private static readonly ConcurrentDictionary<string, byte> s_logged = new();
    private const float QUERY_TIMEOUT_SECONDS = 5.0f;

    public static bool TellZone(IResultContext context, ZONE_102_PROTOCOL.MSG_RAIDCONTROL message) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor is null) {
            return false;
        }

        zoneActor.Tell(message, ActorRefs.NoSender);

        return true;
    }

    /// <summary>
    /// Tells every player in the zone, or just the player the results run for when there is no zone.
    /// </summary>
    public static bool TellEveryone(IResultContext context, object message) {
        if (TellZone(context, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.ToPlayers, Message = message })) {
            return true;
        }

        var playerRef = context.GetPlayerRef();
        playerRef?.Tell(message, ActorRefs.NoSender);

        return playerRef is not null;
    }

    /// <summary>
    /// A step the server knows but does not act on (yet): it logs once and lets the steps after it run.
    /// </summary>
    public static bool Skip(string what) {
        if (s_logged.TryAdd(what, 0)) {
            Logger.Information("Raids | The step {0} is known but not acted on by the server.", Logger.Args(what));
        }

        return true;
    }

    public static Wizard QueryWizard(IActorRef playerRef) {
        if (playerRef is null) {
            return null;
        }

        try {
            return playerRef.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                TimeSpan.FromSeconds(QUERY_TIMEOUT_SECONDS)).Result?.Wizard;
        }
        catch (Exception ex) {
            Logger.Error("Raids | Could not get the wizard for a raid step: {0}", Logger.Args(ex.Message));

            return null;
        }
    }

    /// <summary>
    /// Moves the player to a named location of the zone they are in (a raid's teleport pad, its jail).
    /// </summary>
    public static bool MoveTo(IResultContext context, string locationName) {
        var playerRef = context.GetPlayerRef();
        var zoneActor = context.GetZoneActor();
        if (playerRef is null || zoneActor is null || string.IsNullOrEmpty(locationName)) {
            return false;
        }

        WizZoneData zoneData;
        try {
            zoneData = zoneActor.Ask<ZONE_102_PROTOCOL.MSG_QUERYZONEDATARSP>(new ZONE_102_PROTOCOL.MSG_QUERYZONEDATA(),
                TimeSpan.FromSeconds(QUERY_TIMEOUT_SECONDS)).Result?.ZoneData;
        }
        catch (Exception ex) {
            Logger.Error("Raids | Could not get the zone data to move a player: {0}", Logger.Args(ex.Message));

            return false;
        }

        var location = zoneData?.m_locationList?.FirstOrDefault(l => string.Equals(l?.m_locName, locationName, StringComparison.OrdinalIgnoreCase));
        if (location is null) {
            Logger.Warning("Raids | {0} has no location {1}; the player stays where they are.",
                Logger.Args(context.GetZonePath(), locationName));

            return false;
        }

        var spot = string.Join(",", new[] { location.m_location.X, location.m_location.Y, location.m_location.Z, location.m_direction }
            .Select(v => v.ToString(CultureInfo.InvariantCulture)));
        playerRef.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
        playerRef.Tell(new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = spot }, ActorRefs.NoSender);

        return true;
    }

    /// <summary>
    /// A battleground time: "30m", "10m", "5" (seconds), "1h", "0" (stop).
    /// </summary>
    public static int Seconds(string duration) {
        if (string.IsNullOrWhiteSpace(duration)) {
            return 0;
        }

        duration = duration.Trim().ToLowerInvariant();
        var unit = duration[^1] switch { 'h' => 3600, 'm' => 60, 's' => 1, _ => 0 };
        var number = unit == 0 ? duration : duration[..^1];

        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int) Math.Round(value * (unit == 0 ? 1 : unit))
            : 0;
    }

}

/// <summary>
/// ResBGAdjustTime: sets the raid's timer ("30m" when the raid starts; "0" stops it when it ends). When it runs out the
/// zone posts EVENT_Battleground_EndBattle, which the raid's script ends the raid on.
/// </summary>
internal sealed class ResBGAdjustTimeHandler : BaseResultHandler<ResBGAdjustTime> {

    public override bool Execute(IResultContext context)
        => RaidResult.TellZone(context, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL {
            Kind = RaidControlKind.Timer,
            Seconds = RaidResult.Seconds(Result.m_duration),
        });

}

/// <summary>
/// ResUnknown117B3E95: the end of the raid ("Trigger MATCH_END", and after the last boss's death).
/// </summary>
internal sealed class ResUnknown117B3E95Handler : BaseResultHandler<ResUnknown117B3E95> {

    public override bool Execute(IResultContext context)
        => RaidResult.TellZone(context, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.End });

}

/// <summary>
/// ResUnknown5ABB361D: the raid is won (on the last boss's death).
/// </summary>
internal sealed class ResUnknown5ABB361DHandler : BaseResultHandler<ResUnknown5ABB361D> {

    public override bool Execute(IResultContext context)
        => RaidResult.TellZone(context, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL { Kind = RaidControlKind.Win });

}

/// <summary>
/// ResIncrementBadge: awards the badge (a raid's "defeated" badges), to everyone in the zone with m_allInZone.
/// </summary>
internal sealed class ResIncrementBadgeHandler : BaseResultHandler<ResIncrementBadge> {

    public override bool Execute(IResultContext context) {
        if (string.IsNullOrEmpty(Result.m_badgeName)) {
            return false;
        }

        var award = new CHARACTER_103_PROTOCOL.MSG_BADGEEVENT {
            Kind = BadgeEventKind.Award,
            BadgeId = StringHash.Compute(Result.m_badgeName),
        };

        if (Result.m_allInZone) {
            return RaidResult.TellEveryone(context, award);
        }

        var playerRef = context.GetPlayerRef();
        playerRef?.Tell(award, ActorRefs.NoSender);

        return playerRef is not null;
    }

}

/// <summary>
/// ResDeleteItem: takes items of a template out of the player's backpack (a raid's wisps and coins handed in).
/// </summary>
internal sealed class ResDeleteItemHandler : BaseResultHandler<ResDeleteItem> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        var wizard = RaidResult.QueryWizard(playerRef);
        if (wizard is null) {
            return false;
        }

        var items = wizard.InventoryBehavior.Items
            .Where(item => item.m_templateID == Result.m_itemTemplateID)
            .Take(Math.Max(1, Result.m_quantity))
            .ToList();
        foreach (var item in items) {
            ulong itemId = item.m_globalID;
            if (wizard.RemoveItemFromInventory(itemId)) {
                playerRef.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                    GlobalID = wizard.GameObjectID,
                    ItemID = itemId,
                }, ActorRefs.NoSender);
            }
        }

        return true;
    }

}

/// <summary>
/// ResReduceMana: takes a share of the player's mana (a raid's mana burn pads).
/// </summary>
internal sealed class ResReduceManaHandler : BaseResultHandler<ResReduceMana> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        var wizard = RaidResult.QueryWizard(playerRef);
        if (wizard is null) {
            return false;
        }

        var maxMana = wizard.GameStats.m_baseMana;
        var mana = Math.Clamp(wizard.GameStats.m_currentMana - (int) Math.Round(Result.m_manaPercent * maxMana), 0, maxMana);
        wizard.UpdateMana(mana);
        playerRef.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
            Mana = mana,
            MaxMana = wizard.GameStats.GetClientTypeAlternative().m_baseMana,
        }, ActorRefs.NoSender);

        return true;
    }

}

/// <summary>
/// ResCastSigilSpell: casts the spells into the battles going on in the raid ("cast on Vanguard";
/// Game/Combat/CombatResolver.Raids.cs).
/// </summary>
internal sealed class ResCastSigilSpellHandler : BaseResultHandler<ResCastSigilSpell> {

    public override bool Execute(IResultContext context) {
        var spells = Result.m_spellNameList?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];

        return spells.Length > 0 && RaidResult.TellZone(context, new ZONE_102_PROTOCOL.MSG_RAIDCONTROL {
            Kind = RaidControlKind.SigilSpell,
            Spells = spells,
        });
    }

}

/// <summary>
/// ResUnknown2D21EDCB (m_locationName): sends the player to a location of the zone (Azteca's hazards send a wizard who
/// steps in them to "Jail").
/// </summary>
internal sealed class ResUnknown2D21EDCBHandler : BaseResultHandler<ResUnknown2D21EDCB> {

    public override bool Execute(IResultContext context) => RaidResult.MoveTo(context, Result.m_locationName);

}

/// <summary>
/// ResCinematic: a teleport flash (FX_Teleport_Small before a raid's teleport pad) plays the teleport effect; the
/// other effects are not shown.
/// </summary>
internal sealed class ResCinematicHandler : BaseResultHandler<ResCinematic> {

    public override bool Execute(IResultContext context) {
        if (Result.m_cinematicName?.StartsWith("FX_Teleport", StringComparison.OrdinalIgnoreCase) == true) {
            context.GetPlayerRef()?.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);

            return true;
        }

        return RaidResult.Skip($"ResCinematic {Result.m_cinematicName}");
    }

}

/// <summary>
/// ResUnknown7960FE1A (m_templateID): resets the raid's ritual crystals (the secret boss's); not acted on.
/// </summary>
internal sealed class ResUnknown7960FE1AHandler : BaseResultHandler<ResUnknown7960FE1A> {

    public override bool Execute(IResultContext context) => RaidResult.Skip(nameof(ResUnknown7960FE1A));

}

/// <summary>
/// ResUnknown598D00C0: shown after a raid's bosses fall (the battleground's score board); not acted on.
/// </summary>
internal sealed class ResUnknown598D00C0Handler : BaseResultHandler<ResUnknown598D00C0> {

    public override bool Execute(IResultContext context) => RaidResult.Skip(nameof(ResUnknown598D00C0));

}

/// <summary>
/// ResUnknown4591AF80 (m_points): battleground points for the team; not acted on.
/// </summary>
internal sealed class ResUnknown4591AF80Handler : BaseResultHandler<ResUnknown4591AF80> {

    public override bool Execute(IResultContext context) => RaidResult.Skip(nameof(ResUnknown4591AF80));

}

/// <summary>
/// ResAddEffect: a zone effect on the player (the raids' tick effects); not acted on.
/// </summary>
internal sealed class ResAddEffectHandler : BaseResultHandler<ResAddEffect> {

    public override bool Execute(IResultContext context) => RaidResult.Skip(nameof(ResAddEffect));

}

/// <summary>
/// ResRemoveEffect: takes a zone effect off the player; not acted on.
/// </summary>
internal sealed class ResRemoveEffectHandler : BaseResultHandler<ResRemoveEffect> {

    public override bool Execute(IResultContext context) => RaidResult.Skip(nameof(ResRemoveEffect));

}
