# Ultimus heals smarter (no more standing around)

Changed: When Ultimus needs to heal and has no potion or wisp right where he is, he now looks for the nearest area of the world that has health (or mana) wisps and the nearest potion shop, and goes to whichever is fewer areas away. If it's a tie he picks the wisps, because they're free. He only uses the story's normal ways to get there.
Changed: If an area's wisps are all taken when he arrives, he won't come back to that area for 5 minutes, so he doesn't walk back and forth.
Fixed: He no longer stops to heal when he isn't really low. The "full health before a hard fight" care (a tough creature, a risky plan, a careful mood, or a dungeon) now only uses a potion or a wisp that's right there. If there's none, he goes on, because his health is already above his normal ready level. This was why he stood waiting at 875 of 1,099 health.
Changed: He only waits as a last resort (no potions, no wisps nearby, no potion shop he can reach or afford), and his status now says he's waiting for wisps to come back.

Where: Ultimus (Play > Ultimus). His "Pause and explain" thoughts say which he picked and why.

Test: Let Ultimus run until he's hurt with no potions. Watch him walk to a nearby area for wisps or to a potion shop instead of standing still. Also check he no longer stops to heal when he's above about three quarters health.
