Fixed: One click on a friend no longer teleports you
Sometimes a single click on a friend in the friends list teleported you to them. The game itself decides what a click sends, so the server now asks first with the game's own question, "Do you want to go to your friend?". You only go after you click Yes, so a stray click never teleports you. It is on to start and can be turned off.
Where: Game Rules > Teleport Rooms
Test: Open your friends list and pick a friend who is online (Ultimus works). The question should show; click No and you stay, then try again and click Yes and you go. Untick "Ask before teleporting to a friend" on the Teleport Rooms page and you go at once again, with no restart.

Fixed: "Allow Friend Teleports" is obeyed
The game's own Settings > Privacy > "Allow Friend Teleports" was saved but never checked, so friends could still teleport to a wizard who had it turned off. Now a friend who tries gets "Your friend is busy right now. They do not allow friends to teleport to them." It counts the moment the setting is changed. The words can be changed on the Announcements page.
Test: On one wizard, open Settings > Privacy and untick Allow Friend Teleports. From another wizard who is friends with it, try to teleport to it: you should get the busy box. Tick it again and the teleport works.
