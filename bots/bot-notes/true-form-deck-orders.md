Fixed: True Form read "no damage spells" as attack and could not rebuild his deck

Telling the True Form bot "setup your deck to focus on healing. No damage spells" made him play attack, and "heals only" only lasted one battle. Now a deck order rebuilds his deck right away, or right after the battle, and he keeps it until told otherwise. A heals-only deck has no damage spells, just heals, shields and cleanses, and he plays support. Words after "no", "don't" or "without" are never read as an order to do them. An order with "or" in it, like "blade me or prep for healing", now takes the first choice he understands instead of reading the whole line as a name.

Where: True Form bot and Ultimus chat orders.
Test: Group with True Form and say "heals only", then open his deck: no damage spells. Say "normal deck" to go back. In battle, say "blade me or heal" and he should answer that he will blade you.
