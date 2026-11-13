# Balance spells boosted by Balance gear

Changed: Balance spells that hit as other schools' damage are now boosted by the caster's Balance damage and Balance pierce, as in the real game. This covers Hydra, Spectral Blast and Nested Fury (Fire, Ice or Storm) and Chimera (Life, Death or Myth). Before, the server boosted them with the caster's Fire, Ice or Storm damage instead, so a Balance wizard's damage gear did nothing for them. The target's resist and shields still go by the damage type the hit lands as. Critical hits already went by Balance and are unchanged. This applies to players, Ultimus and creatures alike.

Ultimus's battle planner and gear picker use the same rule, so he now values Balance damage gear for these spells.

Where: battles. No settings.

Test:
1. On a Balance wizard with Balance damage gear (for example +20% Balance damage), cast Hydra on a creature.
2. Each head's damage should be about 20% higher than the card's base before the creature's resist, the same boost a Balance spell like Scarab gets.
3. Cast Hydra on a creature with Fire resist. The Fire head should still be reduced by that resist.
