# Ultimus runs smoother

Changed: He only answers chat that's meant for him. That means a whisper, a line with his name in it, a talk he's already having with you, or something said right next to him while no other players are talking. Players chatting with each other nearby are left alone.
Changed: He remembers known failures across restarts:
- Exits that never take him on (stubborn-exits.txt): the next time he tries once, then goes through at once instead of walking the whole sweep again.
- Shops that don't sell an item after all (not-sold-here.txt).
Changed: He does one thing at a time. A Recall or Commons button press finishes before he plans anything new, and no shopping, selling or training trip starts while he's healing.
New: A replay pack of every bug from your screenshots (the 875/1,099 heal wait, standing still with wisps nearby, potions vs wisps, XP farming, "Hello?", "hatching with someone else", and all 31 chat lines) runs in his self-test after each update. The results go to replay-pack.txt. If a fixed bug ever comes back, it shows up there and in his self-test.
Fixed: The Spectator status line always shows what's true right now: his quest's real step from his quest book, and what he's doing this moment (in a battle, healing, recalling), not an old plan.
Changed: Smoother walking. He curves around corners instead of stopping and turning on them (only where the curve stays on legal ground), and he stops a step short of NPCs, shopkeepers and trainers instead of walking into them. His small wobble never leaves the floor now.
New: A weekly health check (weekly-health.txt) ranks his problems of the last 7 days by what they cost him, biggest first, each with what to do. It covers where time was lost, mistakes, creatures that beat him, stubborn exits, and any screenshot bug that came back. The first one is written when he starts, then once a week.

Where: Ultimus (Play > Ultimus); files in Server\logs\ultimus (replay-pack.txt, weekly-health.txt, stubborn-exits.txt, not-sold-here.txt).

Test: Chat near Ultimus with another player without saying his name: he stays quiet. Say "Ultimus, what step?": he answers. Watch him walk around corners and stop in front of NPCs. After the update, open replay-pack.txt and weekly-health.txt in the Ultimus logs folder.
