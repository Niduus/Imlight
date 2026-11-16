Fixed: Server log colors, filters and repeat counts work again
The server log on the Logs tab did not recognise the lines the server writes to its log file, so it read the whole log as one long line. That left every line the same color, made Hide debug lines, Hide warnings, the kind of line picker and the search show the wrong lines or nothing, and stopped the same message repeated in a row from folding into one line with a count. Ahziael now reads the server's lines the way they are written, so each line gets its own color and the filters work.
Where: Ahziael, Logs tab, Server log.
Test: Open the Server log and check that warnings show in yellow and errors in red, then tick Hide debug lines and pick Errors in the kind of line picker to see only the error lines.
