# Ultimus quests like a human: no more skipped steps

Changed: Ultimus never has a quest step finished for him any more. Before, a step that looked broken (or a spot he could not get out of after every fix) was finished by Ahziael so he could move on. Now he only uses moves that get him unstuck (step aside, another way, hop to a clear spot, back to the area start, through an exit he stands in, or .stuck go to his step's area), just like a player can.
Changed: A step that looks broken is flagged as BROKEN STEP (red) for Niduus, with its debug report, and Ultimus keeps trying it the way a player would until it is fixed.
Changed: A fight he keeps losing is not called broken: he rests and tries again, and it is flagged so you can see it.

Where: Ahziael > Ultimus (his log and debug reports).

Test: Let Ultimus quest. His log should never say "STEP SKIPPED" or "Ahziael finishes the step". When he is stuck for good, look for "BROKEN STEP" or "tried every way to get free" instead.
