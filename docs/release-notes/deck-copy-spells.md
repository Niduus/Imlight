Fixed: Copying spells into a new deck did nothing
Copying the spells from one deck into a newly bought deck failed, because the spells were added while the new deck was still being moved from your backpack into your gear. The server now finishes moving the deck first and then adds the spells, so the copy goes through.
Where: Deck window, copy spells into another deck.
Test: Buy a new deck, equip it, and copy the spells from your old deck into it. All the spells show up in the new deck.
