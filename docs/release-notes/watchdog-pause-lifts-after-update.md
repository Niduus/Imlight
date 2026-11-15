Fixed: Ultimus and the Quest Team carry on by themselves after an update
When the watchdog stops Ultimus or the Quest Team for doing nothing for hours, they used to stay paused until someone pressed Resume, even after an update came in that fixed what stopped them. Now, once a new update is installed, that pause lifts by itself and they carry on. A pause made with Ahziael's Pause button is never lifted this way, and the old stop note is kept as watchdog-stop-old.txt.
Where: Ultimus and the Quest Team, after an update.
Test: Install this update while Ultimus is stopped by the watchdog; within a few seconds of the server starting, his log should say the watchdog's pause is lifted and he should start moving.
