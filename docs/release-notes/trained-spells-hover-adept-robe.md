# Trained Spells hover, and Ultimus back in his Adept robe

Fixed: pointing at a card in the Trained Spells section showed nothing. Now it shows the card big, with how many copies are in the deck. Pointing at a name under "Other spells" shows that spell's card too. The heading now says how many deck cards he knows, how many he uses, and how many copies are in the deck.

Fixed: Ultimus wore Cloak of the Watchful instead of his Adept Sorcerer's Robe. The robe was still in his backpack, but his gear check had read it as worth nothing (its stats were looked up before the game's stat tables were ready, and that empty reading was kept). Now a piece whose stats can't all be read is read again later, and gear he can't fully read is never swapped out. When he changes a piece, the Ultimus log shows the two scores he weighed ("weighs ... against ..."). If a piece still can't be read, the log says so once in orange.

Where: Ahziael > Ultimus and Quest Team pages (Trained spells); Ultimus log.

Test:
1. On the Ultimus page, point at a trained card and at a name under Other spells: the card shows big.
2. Restart the server with Ultimus on: within a minute or two he should put the Adept Sorcerer's Robe back on, and the log says "weighs Adept Sorcerer's Robe (...) against Cloak of the Watchful (...)".
