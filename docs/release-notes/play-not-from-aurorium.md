Fixed: Wizard101 stopping with a "Base Messages" error when you click Play
If Ahziael had been pointed at Aurorium's download folder instead of your real Wizard101, Play started the game from there, and the game stopped at once with a "LoadMessageModule ... Base Messages" error. Play now never starts the game from Aurorium's folder: it finds your real Wizard101 by itself, and if it asks you for the folder, it refuses Aurorium's and tells you which one to pick.
Test: Click Play Wizard101. The game opens to the login screen with no error popup.
