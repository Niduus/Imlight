Fixed: Ahziael closed (and stopped the server) when you clicked a second item
On Database > Items, clicking one item and then another (jewels most of all, since there are so many) closed Ahziael, and the server stopped with it. The same mistake was on the Furniture and Badges tabs. Fixed on all three.
Where: Database > Items
Test: Open Database > Items, pick Jewels under Kind, then click 20 jewels one after another. Ahziael and the server stay running.

New: One logs folder for every crash and error
Ahziael now writes its own errors to a Logs folder by itself, and each time the server stops by itself it leaves a crash report there with the last lines it wrote. Make a zip to send puts it all in one zip on your desktop, with the server logs of the last 3 days, the setup log and Aurorium's logs. Windows user names and internet addresses are blanked out first.
Where: Server > Logs
Test: Open Server > Logs, click Make a zip to send, and check the zip on your desktop. Open logs folder shows the folder.

Changed: A mistake inside Ahziael no longer closes it
If a page in Ahziael hits an error, it now shows a red line, writes the error to the logs folder, and keeps running. If Ahziael ever does close because of an error, the server keeps running so players stay in the game.
