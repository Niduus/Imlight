# Ultimus gets off The Oasis start, and no wasted shop trips

Fixed: Ultimus stood still at the start of The Oasis (Krokotopia) for over an hour. He went back to the start of the area every minute and a half, and each time he landed on the same spot. Now, when he can't find a way on the floor the map shows (a stair or ramp the map has no floor on), he also tries that hidden floor. Walls still stop him. This applies to Ultimus, the Quest Team and Bot Players.

Fixed: Ultimus no longer goes back to the start of an area when he is stuck at the start itself. He walks well away instead, and it is written up once.

New: when Ultimus finds no way on the map to his step, the log now says so once, with both spots: "finds no way on the map from (x, y) to ...".

Fixed: "A Whole New World" was skipped as a "loop" just because Ultimus went through many areas quickly on his way to Krokotopia. Only coming back into the same area again and again counts as a loop now.

Fixed: Ultimus kept trying to buy a Lifebringer's Athame and a Jewel of Instinct from Wul'yahm, who does not sell them. He now checks what the shopkeeper really sells before buying, and Ahziael only gives him gold for a piece when he is at a shop that sells it.

Where: Ultimus (the walking fix also covers the Quest Team and Bot Players).

Test: Take Ultimus to Krokotopia ("Digging in the Dirt"). He should walk from the Spiral Door into the Pyramid to Professor Winthrop. The server log should not show "Player tried to purchase item ... did not have in its inventory" for him.
