# Enemies always show up

Fixed: An enemy could be invisible, leaving an empty spot on the battle circle (the Kraken, for one). This happened when you were away (or on the other side of a teleporter) as a battle started and then came close to watch or join it: the server never sent you the enemy until it died and came back.

New: The server now checks every few seconds that every player near an enemy has been sent that enemy, and sends it again to anyone who is missing it. A wizard who joins a battle also gets every enemy in it a second time, a moment later, in case their game was still busy loading the battle.

Where: Imlight.ini [World] EnemyShowCheckSeconds (default 3 seconds, 0 to 60; 0 turns the check off). Applies live. The server log says "Enemy show check" whenever it had to send a missing enemy.

Test:
1. Let someone (Ultimus works) start a fight with the Kraken while you are on the other side of Triton Avenue.
2. Walk or teleport over to the Kraken and look at the battle circle.
3. The Kraken should be standing on its circle, not an empty spot.
4. Join the battle partway through: every enemy should show on its circle.
