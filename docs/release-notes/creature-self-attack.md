# Enemies never attack themselves

Fixed: The Kraken cast its Storm Beetle on itself. The creature brain reads every card to decide what it does. The pet version of a card (here "Pet - Frost Beetle Storm") also carries a small boost for the caster, so the brain read it as a blade and put it on the Kraken instead of on you.

Fixed two ways:
- The brain now reads any card that deals damage as an attack.
- New server safety net: when an enemy (or a minion) aims an attack, trap, weakness or stun at itself or a teammate, the server moves it onto someone on the other side. A card that can go on a friend (a heal, a blade, a shield) is left alone. Beguile and Confuse still work as before.

Where: no setting. The server log says "... was aimed at itself, on its own side; it goes on slot N instead" whenever the safety net steps in.

Test:
1. Fight the Kraken (or any creature) for several rounds.
2. Its attacks, including beetle-type cards, should always land on your side.
3. Cast Beguile or Confuse on a creature: it should still hit its own side as before.
