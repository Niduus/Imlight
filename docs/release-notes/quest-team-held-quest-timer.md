Fixed: The Quest Team no longer flags a quest it isn't working on as slow
The Quest Team kept a timer for every quest it held. A quest it had set aside kept counting while the team did other quests, so it was flagged as slow or past its time limit for no reason. Now a quest's timer only counts while the team is working on it, the same fix Ultimus got.
Where: Quest Team, no setting.
Test: Let the Quest Team play while it holds a quest it isn't doing. Its log should not show "taking much longer than expected" or "went past the time limit" for that quest.
