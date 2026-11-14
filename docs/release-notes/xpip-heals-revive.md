Fixed: Dryad and other X-pip heals revive a defeated teammate
Healing a defeated teammate with Dryad, another X-pip heal, or a heal that rolls one of several amounts did nothing, because the server threw the card away as "target is dead". Only plain heals like Fairy worked. Now any heal aimed at one teammate brings them back with that heal, like in the real game, for players and creatures. Group heals like Pixie still only heal who is standing, like in the real game.
Where: In battle, no setting.
Test: Fight with two wizards and let one be defeated. Cast Dryad on them from the other: they stand back up. Fairy should still work too.
