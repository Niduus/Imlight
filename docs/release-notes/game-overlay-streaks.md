Fixed: Dark streaks from the overlay on the game
Overlay words could leave thin dark upright streaks over the game picture, like broken bits of text. Windows' coloured text smoothing (ClearType) only works on a solid background. On the see-through overlay it drew dark edges. The overlay now uses plain smoothing for all its words, and every overlay pixel is checked before it goes to Windows. The same fix applies to the "change it on the game" designer.
Where: the overlay over the game (Play > Overlay)
Test: Play with the overlay on, with texts over bright parts of the game. The words should look clean, with no dark lines or bits of letters around them.
