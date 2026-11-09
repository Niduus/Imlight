Fixed: Health jumping around in Intercept fights
Intercepts (Shadow Sentinel, and the Druids in the Renegade Druid fight) now work the way the game itself works them out: the interceptor is hit when the whole spell is over and keeps intercepting until then, it only uses its resist, and several interceptors take their share in the order they cast. Before, the server and the game disagreed on who got hurt, so health jumped at the next round and some fighters showed as defeated while still in the fight.
Test: Fight Renegade Druid in the Catacombs, hit him from round 2 on with single hits and with an AoE, and watch that the health numbers stay put when the next round starts.

Fixed: Shadow backlash hit uses the game's math
A backlash hit now takes a share of your maximum health (1% for each point of backlash), lowers your backlash by that share instead of wiping it, and never defeats you. Backlash stays between 0 and 100, like in the game.
Test: Cast shadow pact spells without feeding them, let the last pact run out, and check the chat line "Backlash! You lose ..." against your health.
