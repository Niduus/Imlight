Fixed: Server log in Ahziael keeps updating
The server log on the Logs tab stopped showing new lines while the server was running, and only caught up after a restart or a new day. Windows does not refresh the size and time it lists for a file that another program still has open, and Ahziael looked at those old numbers to tell if the log had changed. Ahziael now opens the log itself to see its real size and time, so new lines show every 2 seconds.
Where: Ahziael, Logs tab, Server log.
Test: Open the Server log, leave the server running and do something in game, such as walking into another area, and watch new lines appear without a restart.
