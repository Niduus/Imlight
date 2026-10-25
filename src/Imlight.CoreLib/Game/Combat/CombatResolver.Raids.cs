using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// A raid's sigil spells (Game/Raids, ResCastSigilSpell): when the players outside finish a part of the raid, its script
/// casts a spell "on Vanguard", into the battles going on in the raid. A battle of the players who were in the raid then
/// casts each one at the start of its next round, free, for the players' side (Raid_Sigil_01: 500 Death damage to the
/// creatures).
/// </summary>
public partial class CombatResolver {

    private readonly HashSet<long> _raidSigilCasts = [];

    private float RaidSigilSpells(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var players = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c._wizard is not null
            && c.OccupiedTeam == CombatTeam.Player).ToList();
        if (players.Count == 0) {
            return 0;
        }

        var casts = Raids.Raids.SigilCastsFor(players.Select(p => p._wizard.CharId), _raidSigilCasts);
        var cinematicTime = 0.0f;
        foreach (var cast in casts) {
            _raidSigilCasts.Add(cast.Id);
            var caster = players.FirstOrDefault(p => p.IsAlive) ?? players[0];
            foreach (var name in cast.Spells) {
                var (spell, template) = FindSpell(name, 0);
                if (template is null) {
                    Logger.Warning("Duel {0} | Raid sigil spell \"{1}\" not found.", Logger.Args(_duel.m_duelID.Full, name));

                    continue;
                }

                var target = _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive
                    && c.OccupiedTeam != caster.OccupiedTeam);
                if (target is null) {
                    continue;
                }

                spell = spell with { m_pipCost = new SpellRank() };
                caster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                var before = combatActionList.m_actionList.Count;
                var action = new QueuedCombatAction { SpellCaster = caster, SelectedTarget = target, Spell = spell, SpellTemplate = template };
                cinematicTime += HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
                ShowFree(spell, template, combatActionList, before);
                Logger.Information("Duel {0} | Raid sigil spell {1} cast from slot {2} ({3}).",
                    Logger.Args(_duel.m_duelID.Full, name, caster.SlotIndex, cast.ZonePath));
            }
        }

        return cinematicTime;
    }

}
