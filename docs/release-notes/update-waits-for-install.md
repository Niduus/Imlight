# Updates: no false "could not be built"

Fixed: When a new version came out while the last update was still being installed (Ahziael makes a backup first, which can take a minute or two), Ahziael said "Imlight ... could not be built" and setup-log.txt was empty. Nothing was broken. Now it says the update waits until the other one is installed. The setup log also says when the backup before an update starts.

Where: Ahziael, the update panel at the top of the window.

Test:
1. When Ahziael updates by itself, watch the message at the top. You should not see "could not be built" unless a build really failed.
