Fixed: Temple of Storms teleporter works before the tests are done
The teleporter down to the boss in the Temple of Storms in Krokotopia took you down before you passed both tests, even though it looked switched off. This happened to wizards who had finished the Temple once before, because the dungeon looked at their old run instead of this one. Now every run of a dungeon checks its own tests, so the teleporter only works once both tests are passed in this run.
Where: Krokotopia, Temple of Storms.
Test: Enter the Temple of Storms with a wizard who has finished it before, step on the teleporter before passing both tests and check it does nothing, then pass both tests and check it takes you down.
