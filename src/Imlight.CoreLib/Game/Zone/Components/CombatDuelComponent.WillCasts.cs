/*
 * Pet Will Casts in a battle (Niduus, 2026-10-27), see Game/Pet/PetWillCasts.cs.
 *
 * At the start of each planning phase every wizard whose pet has a Will Cast is sent MSG_SHOWPETCARD for the game's Will
 * Cast button: the card, the rounds left before it can be used again (its ramp-up at the start of the battle, its
 * cooldown after a use), and whether it can't be used now (no uses left this battle, too little happiness, PvP or still
 * locked). Pressing the button sends MSG_PETWILLCAST; `.willcast` does the same from chat. The card then casts for free
 * before the wizard's own card this round (the .c1 queue), its happiness cost comes off the pet, and the button is sent
 * again with its new cooldown.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    // Per wizard (global id): the round they were first seen in planning, and per Will Cast talent its uses and the
    // round of its last use.
    private sealed class WillCastState {
        internal int FirstRound;
        internal readonly Dictionary<uint, (int Uses, int LastRound)> Used = [];
    }

    private readonly Dictionary<ulong, WillCastState> _willCasts = [];

    private int WillCastRound => (int) Duel.m_roundNum;

    private WillCastState WillCastStateOf(CombatDuelSubCircle circle) {
        var id = (ulong) circle.ParticipantObject.m_globalID;
        if (!_willCasts.TryGetValue(id, out var state)) {
            _willCasts[id] = state = new WillCastState { FirstRound = Math.Max(1, WillCastRound) };
        }

        return state;
    }

    /// <summary>Rounds left before a Will Cast can be used: its ramp-up in a new battle, then its cooldown after each use.</summary>
    private int WillCastCooldown(CombatDuelSubCircle circle, PetWillCasts.WillCast willCast) {
        var state = WillCastStateOf(circle);
        var roundsIn = WillCastRound - state.FirstRound + 1;
        var left = willCast.RampUp - roundsIn;
        if (state.Used.TryGetValue(willCast.TalentId, out var used) && used.Uses > 0) {
            left = Math.Max(left, used.LastRound + willCast.Cooldown - WillCastRound);
        }

        return Math.Max(0, left);
    }

    /// <summary>Why a Will Cast can't be used now, in words for the player; null when it can.</summary>
    private string WillCastProblem(CombatDuelSubCircle circle, PetWillCasts.WillCast willCast) {
        if (!willCast.Usable) {
            return $"{willCast.ShownName} is still locked. Unlock it with reagents first (.willcast unlock).";
        }

        if (willCast.NoPvP && IsPvPDuel) {
            return $"{willCast.ShownName} can't be used in PvP.";
        }

        var state = WillCastStateOf(circle);
        if (willCast.UsesPerCombat > 0 && state.Used.TryGetValue(willCast.TalentId, out var used) && used.Uses >= willCast.UsesPerCombat) {
            return $"{willCast.ShownName} was used {used.Uses} times this battle, its most.";
        }

        if (PetWillCasts.CostsHappiness && willCast.HappinessCost > 0) {
            var happiness = PetProgress.GetBehavior(PetWillCasts.WornPet(circle._wizard))?.m_happiness ?? 0;
            if (happiness < willCast.HappinessCost) {
                return $"{willCast.ShownName} needs {willCast.HappinessCost} pet happiness and your pet has {happiness}. Feed it snacks.";
            }
        }

        var cooldown = WillCastCooldown(circle, willCast);

        return cooldown > 0 ? $"{willCast.ShownName} can be used again in {cooldown} round{(cooldown == 1 ? "" : "s")}." : null;
    }

    // Called at the start of each planning phase.
    private void SendPetCards() {
        if (!PetWillCasts.Enabled) {
            return;
        }

        foreach (var circle in SubCircles.Where(c => c is not null && c.Occupied && c.IsHumanPlayer && c.AddedToDuel && c.IsAlive
                                                     && c._wizard is not null && c.ParticipantActor is not null)) {
            try {
                SendPetCard(circle);
            }
            catch (Exception e) {
                Logger.Error("Duel {0} | Slot {1} | Pet Will Cast card could not be sent: {2}", Logger.Args(SigilId, circle.SlotIndex, e));
            }
        }
    }

    private void SendPetCard(CombatDuelSubCircle circle) {
        var willCasts = PetWillCasts.ForWizard(circle._wizard);
        if (willCasts.Count == 0) {
            return;
        }

        // The game shows one pet card: the one ready soonest, an unlocked one before a locked one.
        var shown = willCasts.OrderBy(w => w.Usable ? 0 : 1).ThenBy(w => WillCastProblem(circle, w) is null ? 0 : 1)
            .ThenBy(w => WillCastCooldown(circle, w)).First();
        if (PetCardData(shown) is not { } data) {
            return;
        }

        var cooldown = WillCastCooldown(circle, shown);
        var failed = WillCastProblem(circle, shown) is not null && cooldown == 0;
        circle.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWPETCARD {
            PetData = data,
            Cooldown = cooldown,
            RequirementFailed = failed ? 1 : 0,
            MyTurn = 1,
        });
        Logger.Debug("Duel {0} | Slot {1} | Pet Will Cast card {2} ({3}) sent as {4}: cooldown {5}, requirement failed {6}.",
            Logger.Args(SigilId, circle.SlotIndex, shown.Talent, shown.SpellName, PetWillCasts.CardData, cooldown, failed));
    }

    private ByteString? PetCardData(PetWillCasts.WillCast willCast) {
        switch (PetWillCasts.CardData.ToLowerInvariant()) {
            case "spellname":
                return (ByteString) Encoding.ASCII.GetBytes(willCast.SpellName);
            case "talentname":
                return (ByteString) Encoding.ASCII.GetBytes(willCast.Talent);
            default:
                var spell = SpellFactory.GetSpell(willCast.SpellTemplateId);
                if (spell is null || !_serializer.Serialize(spell, _combatParticipantHandFlags, out var buffer)) {
                    Logger.Error("Duel {0} | Pet Will Cast card {1} could not be serialized.", Logger.Args(SigilId, willCast.SpellName));

                    return null;
                }

                return buffer;
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PETWILLCASTREQUEST))]
    private void ReceivePetWillCastRequest(COMBAT_106_PROTOCOL.MSG_PETWILLCASTREQUEST message) {
        var circle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        void Inform(string text) => message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text });
        if (!_isActive || circle?._wizard is null) {
            return;
        }

        try {
            var willCasts = PetWillCasts.Enabled ? PetWillCasts.ForWizard(circle._wizard) : [];
            if (willCasts.Count == 0) {
                Inform(PetWillCasts.Enabled ? "Your pet has no Will Casts." : "Will Casts are turned off on this server.");

                return;
            }

            if (message.List) {
                Inform("Your pet's Will Casts: " + string.Join("; ", willCasts.Select((w, i)
                    => $"{i + 1}. {w.ShownName}: {WillCastProblem(circle, w) ?? "ready"}")) + ". Use .willcast <number> to cast one.");

                return;
            }

            var willCast = message.Talent != 0 ? willCasts.FirstOrDefault(w => w.TalentId == message.Talent)
                : FromGameData(willCasts, message.GameData)
                  ?? willCasts.FirstOrDefault(w => WillCastProblem(circle, w) is null) ?? willCasts[0];
            if (willCast is null) {
                return;
            }

            if (!circle.IsAlive) {
                Inform("You can't use a Will Cast while defeated.");

                return;
            }

            if (Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
                Inform($"Use {willCast.ShownName} while you pick your card.");

                return;
            }

            if (WillCastProblem(circle, willCast) is { } problem) {
                Inform(problem);
                SendPetCard(circle);

                return;
            }

            var target = WillCastTarget(circle, willCast, message.Target);
            var count = CombatResolver.AddFreeDelayedCast(circle, willCast.SpellTemplateId, true, target);
            if (count == 0) {
                Inform($"{willCast.ShownName} could not be cast.");

                return;
            }

            var state = WillCastStateOf(circle);
            var uses = state.Used.TryGetValue(willCast.TalentId, out var used) ? used.Uses : 0;
            state.Used[willCast.TalentId] = (uses + 1, WillCastRound);
            var happinessLeft = TakeWillCastHappiness(circle, willCast);
            Logger.Information("Duel {0} | Slot {1} | Pet Will Cast {2} casts {3} on {4} this round (use {5}, happiness left {6}).",
                Logger.Args(SigilId, circle.SlotIndex, willCast.Talent, willCast.SpellName, target ?? "its usual target", uses + 1,
                    happinessLeft?.ToString() ?? "unchanged"));
            Inform($"Your pet casts {willCast.ShownName} before your card this round"
                + (willCast.UsesPerCombat > 0 ? $" ({willCast.UsesPerCombat - uses - 1} uses left this battle)" : "")
                + (happinessLeft is { } left ? $". Pet happiness: {left}." : "."));
            SendPetCard(circle);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Pet Will Cast failed: {1}", Logger.Args(SigilId, e));
        }
    }

    /// <summary>The Will Cast the game's button names: by spell or talent name in the text, else as a serialized card.</summary>
    private PetWillCasts.WillCast FromGameData(List<PetWillCasts.WillCast> willCasts, byte[] data) {
        if (data is null || data.Length == 0) {
            return null;
        }

        var text = Encoding.ASCII.GetString(data);
        var named = willCasts.FirstOrDefault(w => text.Contains(w.SpellName, StringComparison.OrdinalIgnoreCase)
                                                 || text.Contains(w.Talent, StringComparison.OrdinalIgnoreCase));
        if (named is not null) {
            return named;
        }

        try {
            if (_serializer.Deserialize<Spell>(data, _combatParticipantHandFlags, out var spell) && spell is not null) {
                return willCasts.FirstOrDefault(w => w.SpellTemplateId == spell.m_templateID);
            }
        }
        catch (Exception) {
            // Not a card; the first ready Will Cast is used.
        }

        foreach (var willCast in willCasts) {
            var id = BitConverter.GetBytes(willCast.SpellTemplateId);
            for (var i = 0; i + 4 <= data.Length; i++) {
                if (data.AsSpan(i, 4).SequenceEqual(id)) {
                    return willCast;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The .c1 target code (pN / eN) of the circle the game's button picked, when it fits the card's side; null lets
    /// the card go where it usually does (an enemy card at the enemy the wizard picked, anything else on the wizard).
    /// </summary>
    private string WillCastTarget(CombatDuelSubCircle caster, PetWillCasts.WillCast willCast, int target) {
        if (target < 0) {
            return null;
        }

        // Like MSG_COMBATMOVE: a single bit is a circle's bit, anything else the circle's number.
        var slot = target > 0 && (target & (target - 1)) == 0 ? (int) Math.Log2(target) : target;
        var circle = SubCircles.FirstOrDefault(c => c is not null && c.SlotIndex == slot);
        if (circle is null || !circle.Occupied) {
            return null;
        }

        var ownSide = circle.SlotType == caster.SlotType;
        var enemyCard = (CoreObjectFactory.GetCoreTemplate(willCast.SpellTemplateId) as SpellTemplate)?.m_effects?
            .Any(e => e is not null && e.m_effectTarget is kEffectTarget.kEnemySingle or kEffectTarget.kPreselectedEnemySingle
                                                         or kEffectTarget.kAtLeastOneEnemy) == true;
        if (enemyCard == ownSide) {
            return null;
        }

        var side = SubCircles.Where(c => c is not null && (c.SlotType == caster.SlotType) == ownSide).OrderBy(c => c.SlotIndex).ToList();
        var number = side.IndexOf(circle) + 1;

        return number is >= 1 and <= 4 ? $"{(ownSide ? 'p' : 'e')}{number}" : null;
    }

    /// <summary>Takes a Will Cast's happiness off the wizard's pet and tells the game; returns the happiness left, or null.</summary>
    private uint? TakeWillCastHappiness(CombatDuelSubCircle circle, PetWillCasts.WillCast willCast) {
        if (!PetWillCasts.CostsHappiness || willCast.HappinessCost <= 0) {
            return null;
        }

        var pet = PetWillCasts.WornPet(circle._wizard);
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return null;
        }

        var left = (uint) Math.Max(0, (long) behavior.m_happiness - willCast.HappinessCost);
        PetProgression.SetHappiness(pet, left);
        circle._wizard.SavePet(pet);
        if (PetBehaviorUpdate.TryCreate(pet, out var update)) {
            circle.ParticipantActor.Tell(update);
        }

        return left;
    }

}
