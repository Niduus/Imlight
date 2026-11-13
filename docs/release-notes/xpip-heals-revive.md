# Every single-teammate heal revives

Fixed: Healing a defeated teammate with Dryad (or any other X-pip heal, or a heal that rolls one of several amounts) did nothing: the server threw the card away as "target is dead". Only plain heals like Fairy brought a teammate back. Now any heal aimed at one teammate revives them with that heal, as in the real game, for players and creatures alike. Revive spells hidden inside the same kind of card (an X-pip or random revive) now work on a defeated teammate too.

Where: battle, no setting. Heals on a whole team (Pixie-style group heals) still only heal who is standing, as in the real game.

Test:
1. Fight with two wizards (a Bot Player or your second account works).
2. Let one of them be defeated.
3. On the other one, cast Dryad on the defeated teammate.
4. They should stand back up with the healed health.
5. Do the same with Fairy: it should still revive too.
