Fixed: Ultimus's slow-quest note no longer guesses a wrong cause
When a quest took Ultimus longer than expected, his note guessed what was blocking it, sometimes "with high confidence". When he was really just in a battle, or busy with another quest, the guess was wrong. A common one was "the wizard lacks something the quest requires". Now, if he is in a battle or working on another quest, the note says that plainly and calls the quest slow, not blocked.
Where: Ultimus's log and debug reports, no setting.
Test: Watch Ultimus fight while one of his quests runs long. Its "taking much longer than expected" note should end with "He is in a battle right now, so this quest is only slow, not blocked."
