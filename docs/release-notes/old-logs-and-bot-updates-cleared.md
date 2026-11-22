Changed: Old server logs and old bot updates are cleared by themselves

The server kept every daily log and every bot update it ever downloaded, which had grown past 2 GB. It now keeps the newest 7 daily logs (set [Logging] KeepLogFiles in Imlight.ini to keep more) and the bot update that is running plus the 3 newest, and deletes the older ones by itself. The bots' own log folders and their learned data are not touched.

Where: the server's logs folder and bots\updates folder.
Test: After this update, start the server: the logs folder holds at most 7 daily logs, and bots\updates holds 3 or 4 bot update folders.
