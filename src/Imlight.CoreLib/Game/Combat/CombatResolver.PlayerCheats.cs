/*
 * Free casts of a cheat (Niduus, 2026-10-22): the FreeCast Do of the player cheats (spiraldb-extra/PlayerCheats.json, see
 * PlayerCheats.cs), which a boss's cheats can use too (Niduus, 2026-10-23: "ALL CHEATS are to be compatible for PLAYERS
 * and CREATURES"). The player cheats themselves go through the boss cheats' Whens, Ifs and Dos (BossesWithCheats).
 * Their spells are the .c1 / .c2 free casts (AddFreeDelayedCast), put in the queue the cheat's Timing names; "Now" casts
 * at once through its own queue ('x').
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // The queue of a cheat's "Now" free casts: cast as soon as they are queued.
    private const char NOW_QUEUE = 'x';

    // A cheat's Target as a .c1 target: me, p1-p4, e1-e4 and all as they are; Self, AllEnemies and AllAllies as me and
    // all; any other (the attacker, the weakest player...) as the circle it names now. Null: as the spell aims.
    private string FreeCastTarget(CombatDuelSubCircle me, string target, SpellTemplate template, CombatDuelSubCircle trigger,
                                  CombatDuelSubCircle receiver) {
        var code = (target ?? "").Trim().ToLowerInvariant();
        switch (code) {
            case "":
                return null;
            case "me" or "self":
                return "me";
            case "all" or "allenemies" or "allallies":
                return "all";
            case { Length: 2 } when code[0] is 'p' or 'e' && code[1] is >= '1' and <= '4':
                return code;
        }

        return StepTargets(me, target, template, trigger, receiver).FirstOrDefault() is { } circle ? CodeOf(me, circle) : null;
    }

    private static bool IsEveryone(string target) => (target ?? "").Trim().ToLowerInvariant() is "everyone" or "everyoneelse";

    // The .c1 code of a circle as the caster sees it: pN on its own side, eN on the other (FreeTargetCircle the other way).
    private static string CodeOf(CombatDuelSubCircle caster, CombatDuelSubCircle circle) {
        if (circle == caster) {
            return "me";
        }

        var ownSide = circle.SlotType == caster.SlotType;
        var side = caster._duelActor.SubCircles
            .Where(c => c is not null && (c.SlotType == caster.SlotType) == ownSide)
            .OrderBy(c => c.SlotIndex).ToList();
        var number = side.IndexOf(circle);

        return number is >= 0 and < 4 ? $"{(ownSide ? 'p' : 'e')}{number + 1}" : null;
    }

    // FreeCast: Times free casts of the spell in the queue Timing names (see PlayerCheats.cs).
    private float QueuePlayerCast(CombatDuelSubCircle me, BossCheats.CheatAction step, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                                  CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var (spell, template) = FindSpell(step.Spell, step.SpellID);
        if (spell is null) {
            Logger.Warning("Duel {0} | Cheat free cast spell \"{1}\" not found.", Logger.Args(_duel.m_duelID.Full, step.Spell.Length > 0 ? step.Spell : step.SpellID));

            return 0;
        }

        // First and Last are the round's first and last moves (the next round's first when the round is already under
        // way); Before and After are around its own card (the next round's when it has been played); Now is at once.
        var timing = (step.Timing ?? "").Trim().ToLowerInvariant();
        var (atStart, when) = timing switch {
            "first" when !_cheatAtRoundStart => (true, 'f'),
            "last" => (true, 'l'),
            "before" when !_cheatOwnCard => (true, 'n'),
            "after" => (false, 'n'),
            _ => (true, NOW_QUEUE),
        };
        var count = Math.Clamp(step.Times, 1, 10);
        // OnTarget (Niduus, 2026-10-26): a spell that only goes on its caster goes on the Target, once on each one it
        // names (every ally, every enemy...), as a .c1 on that circle. Everyone and EveryoneElse (both sides) cast once on
        // each one standing too, with or without OnTarget.
        var onTarget = step.OnTarget && (step.Target ?? "").Trim().Length > 0;
        // A spell that only goes on its caster, without OnTarget, goes on the caster once (Niduus's log, 2026-10-25: Brace on
        // EveryoneElse cast once for each of them, all four on him, and said nothing about it).
        var selfOnly = !onTarget && (template.m_effects ?? []).Count > 0 && template.m_effects.All(e => e?.m_effectTarget == kEffectTarget.kSelf);
        if (selfOnly && (step.Target ?? "").Trim().Length > 0 && FreeCastTarget(me, step.Target, template, trigger, receiver) != "me") {
            Logger.Information("Duel {0} | Slot {1} | Cheat free cast {2}: it only goes on its caster, so it goes on slot {1}, not on {3} "
                               + "(\"self spells go on the target\" puts it on them).",
                Logger.Args(_duel.m_duelID.Full, me.SlotIndex, (string) template.m_name, step.Target));
        }

        var targets = onTarget || IsEveryone(step.Target) && !selfOnly
            ? StepTargets(me, step.Target, template, trigger, receiver).Where(c => c is not null).Select(c => CodeOf(me, c)).Where(c => c is not null).ToList()
            : [selfOnly ? "me" : FreeCastTarget(me, step.Target, template, trigger, receiver)];
        if (targets.Count == 0) {
            Logger.Information("Duel {0} | Slot {1} | Cheat free cast {2}: no one stands where its Target \"{3}\" aims.",
                Logger.Args(_duel.m_duelID.Full, me.SlotIndex, (string) template.m_name, step.Target));

            return 0;
        }

        foreach (var target in targets) {
            for (var i = 0; i < count; i++) {
                if (AddFreeDelayedCast(me, spell.m_templateID, atStart, out _, target, step.Silent, when, step.Enchant, step.Enchants,
                        onTarget, cheat: true) == 0) {
                    return 0;
                }
            }
        }

        if (when != NOW_QUEUE) {
            return 0;
        }

        var time = CastDelayed(me, atStart: true, combatActionList, cinematicTimeSoFar, NOW_QUEUE);
        // None of them waits for later (one that found no target is dropped).
        _delayedCasts.RemoveAll(d => d.When == NOW_QUEUE && d.Action.SpellCaster == me);

        return time;
    }

}
