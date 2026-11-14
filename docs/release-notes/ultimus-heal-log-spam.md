Fixed: Ultimus's log no longer repeats the same heal line
When Ultimus was hurt and walked on, his log wrote "is hurt ... heals before going on" twice a second for minutes, over a thousand lines in one morning. It is now written at most once a minute.
Where: Ultimus log, no setting.
Test: Let Ultimus get hurt and keep playing. His log should show that line now and then, not every half second.
