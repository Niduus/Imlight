# Ultimus buys the better robe and steps off raised lawns

Fixed: Ultimus kept wearing Cloak of the Watchful instead of the Adept Sorcerer's Robe. The robe costs 21,880 gold in the shop and he only had about 4,600, so the shop trip never offered it. Now, when a shop piece is a clear upgrade (much better than what he wears) and he cannot afford it, Ahziael gives him just enough gold to buy it, then he goes and buys it. The Ultimus log notes the gift in its COMMAND color.

Fixed: Ultimus stood still by the "To Lower Triton" teleporter in Triton Avenue. He was on the raised lawn there, and his walking path went straight over the edge, which the game does not let you walk down. Now a ledge too high to climb or drop splits the area into separate parts, so he knows he must use a teleporter to get off. If stepping aside does not free him, he now also tries the nearest teleporter on his own part of the area before being sent back to the area start.

Where: no new settings.

Test:
1. Let Ultimus quest with little gold near a shop that sells a much better piece for his level: he should get the gold and wear the new piece.
2. Use Ultimus tp while he is near the "To Lower Triton" teleporter: he should take the teleporter instead of standing still.

Fixed: Ultimus could be stuck behind the low stone fence by the houses in Triton Avenue, pressed on a wall. When he stands on a small patch cut off by a fence or a ledge with no teleporter on it, he now goes straight back to the area start instead of trying to step aside three times first.

Changed: the routes now follow the Minimap. Where the Minimap is black there is no floor, and no route goes there any more (before, the route finder treated it like plain ground, so a line could cut across it). This one route finder is shared: the Minimap's quest line, Ultimus and the Quest Team all use it, so all three now keep to ground you can really walk.

Test (more):
3. Open the Minimap with the Quest Tracker on in Triton Avenue: the red line should never cross a black area.
