Fixed: A new wizard's deck and tutorial steps are no longer lost
When a new wizard skipped the tutorial, the game sent a big burst of "add this card to my deck" messages all at once. The server's flood guard threw 164 of them away, along with 2 tutorial steps, so the new wizard could start with a deck that was missing cards. Deck cards and tutorial steps are now never thrown away.
Where: Making a new wizard, no setting.
Test: Make a new wizard and skip the tutorial. Open the deck once you land: every starting card should be there.
