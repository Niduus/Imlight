Fixed: Claude's smaller game pictures
When Claude asked Ahziael for a game picture smaller than the game window, it failed with "Invalid source bitmap type". Ahziael now shrinks the picture itself, so pictures of any size work. Full-size pictures already worked.
Where: Ahziael's Claude connector, take_game_picture.
Test: in Claude desktop, ask Claude to use Ahziael to take a picture of your game window; it shows the game.
