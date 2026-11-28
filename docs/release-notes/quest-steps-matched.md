Fixed: Quest steps do the right thing when finished
The quests rebuilt from the game's own files are rebuilt again. The earlier pass matched steps by their number, so some steps got actions that belonged to a different step, such as storms, blockers or cages changing at the wrong time. Steps are now matched by their place and target, and the obsidian stones in Xibalba get the actions they were missing.
Where: quests in the later worlds, including Mirage, Empyrea, Zafaria and Xibalba.
Test: play the Xibalba obsidian stone quest and a Mirage storm quest and check that the stones and storms change when the step is done.
