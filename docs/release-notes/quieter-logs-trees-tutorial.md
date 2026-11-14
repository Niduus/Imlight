Fixed: Fewer repeat lines in the server log from Ravenwood and new wizards
Each visit to Ravenwood wrote five warnings that the school trees have no shop. Each new wizard who skipped the tutorial wrote six "could not remove quest" warnings for tutorial quests they never had. Neither was a problem. The tree line is now written once per tree until the server restarts. The tutorial line only shows when a wizard really holds the quest and it still won't go.
Where: The server's daily log, no setting.
Test: Visit Ravenwood twice and make a new wizard who skips the tutorial. The daily log should show the tree line only once per tree and no "could not remove quest" lines.
