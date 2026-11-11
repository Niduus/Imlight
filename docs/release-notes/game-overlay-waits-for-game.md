Fixed: The overlay showed over Ahziael with no game open
The Ahziael line and the sparkle button could show up over Ahziael's own window (or another window) when Wizard101 wasn't on the screen. The overlay now waits until Wizard101 (WizardGraphicalClient.exe) is running and its window has been on the screen for a moment, and only then draws, right on top of the game. If Windows won't put it directly over the game, it stays hidden and tries again. When the game closes, the overlay goes away at once.
Where: Play > Overlay (and Ahziael Overlay on its own)
Test: Open Ahziael without the game: nothing is drawn anywhere. Click Play: the line shows on the game a couple of seconds after its window appears. Close the game: the line goes away straight away.
