Fixed: Creatures cast X pip spells on themselves
Creatures aimed spells that cost X pips (Judgement, Heck Hound, Tempest, Backdraft and the like) at themselves instead of at you. They did not see these cards as attacks or traps, so they treated them like a buff for themselves. Now they aim them at a wizard on your side, with the normal monster brain and with the Rewritten AI.
Where: In the game, any battle against creatures that use X pip spells
Test: Fight a Balance or Fire creature that has Judgement, Heck Hound or Backdraft (for example with .unlearnboss or a custom deck). When it casts one, the spell goes on you or a teammate, never on the creature.
