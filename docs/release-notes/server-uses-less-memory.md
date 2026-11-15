Fixed: The server uses much less memory
The server read every game archive whole into memory, though 98% of an area's archive is 3D models and sounds only the game draws, and it kept Root.wad in a buffer almost twice its size. It now keeps only the parts it reads, about 330 MB less right away and far less while Ultimus, the Quest Team and the bots read hundreds of areas. The helper programs that run Ultimus, the Quest Team, the Bot Players and the Bazaar bot now use the small memory mode, and the server hands memory it no longer uses back to the PC sooner.
Where: Task Manager > Details > Imlight.Director.exe
Test: Start the server with Ultimus and the Quest Team on and let them play for 20 minutes. Imlight.Director.exe should stay well under the 9 GB it reached before.
