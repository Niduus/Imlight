Fixed: Two updates at once no longer break Ahziael
When you clicked Update Ahziael while a newer version was still coming out, the update built its server from the newest code on GitHub instead of its own version, and the next update could start on top of it. Two versions got mixed together and Ahziael stopped working. Now each update builds exactly its own version, only one update runs at a time (a newer one waits its turn), a download is only used when it is complete, and the Ahziael program is swapped in only once it is fully copied.
Where: Ahziael, the update panel at the top of the window
Test: Click Update Ahziael right after a new version comes out, and click it again while it says Downloading or Building. It says the update is still going instead of starting a second one. When it restarts, Ahziael opens and the server starts normally.

Fixed: An update that stopped halfway is finished by itself
If the PC turned off or Ahziael closed while an update was putting the new server in, the server could be left half old, half new. The next time the server starts (or the next update runs) it finishes putting in the same update. If the server was built by a different version than Ahziael, Ahziael builds it again for its own version in the background.
Where: Ahziael, the update panel at the top of the window
Test: Nothing to do unless an update was ever cut off. After any update, start the server and check that you can log in and play.
