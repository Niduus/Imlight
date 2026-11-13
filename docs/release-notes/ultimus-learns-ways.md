# Ultimus acts sooner when idle and learns the ways that work

Changed: Ultimus now acts 25% sooner when it stands still or stops making progress. It counts as stuck after about 56 seconds of standing still instead of 75. The "no progress" timer runs at three quarters of the Stuck Minutes setting, so 6 minutes when the setting is 8.

New: when Ultimus makes no progress for that long, it now does something about it. The first time, it looks at the way fresh and tries another way, such as a teleporter. If that didn't help, the next time it goes back to the start of the area and finds the way from there.

New: Ultimus and the Quest Team write down every walk that got them to their quest step: which street, where from, where to, and the spots they walked. They also write down every spot where they got stuck. The next time a bot (Ultimus, the Quest Team or Bot Players) walks from about there to about there, it takes the proven walk. Other walks steer around the stuck spots. A proven walk that later gets a bot stuck stops counting. Everything is kept in logs/routes/learned-routes.json, so it survives restarts and updates.

Where: Ultimus, the Quest Team and Bot Players. Nothing to set up.

Test: Let Ultimus play for a while. Its log should show lines like "remembers the walk to ... as a way that works (N known ways, M stuck spots to walk round)". If it stands still, it should step aside after about a minute, not a minute and a quarter. After a server restart, logs/routes/learned-routes.json should still be there and keep growing.
