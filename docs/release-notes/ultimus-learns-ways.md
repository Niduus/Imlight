Changed: Ultimus notices he is stuck sooner
Ultimus now counts as stuck after about 56 seconds of standing still instead of 75. When he stops making progress, he first tries another way (such as a teleporter), and if that doesn't help, he goes back to the start of the area and tries from there.
Where: Ahziael > Ultimus
Test: If Ultimus stands still, he should move off after about a minute.

New: Bots remember walks that worked
Ultimus and the Quest Team write down every walk that got them to their quest step, and every spot where they got stuck. Next time, any bot walking the same way takes the walk that worked and goes around the stuck spots. It is saved in logs\routes\learned-routes.json, so it survives restarts and updates.
Where: Ahziael > Ultimus
Test: Let Ultimus play for a while. His log should say "remembers the walk to ...". After a restart, learned-routes.json should still be there.
