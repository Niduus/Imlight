Changed: Updates get ready much faster
When an update builds the new server, it now reuses the last build and only builds again what changed. A usual server fix was ready in about 36 seconds instead of over 2 minutes in a test, and the result is exactly the same as a full build. If the quick build ever fails, Ahziael builds the whole server again by itself. The backup made while an update installs is also packed the quick way, so updates go in sooner.
Where: Server > Ahziael
Test: After the next update after this one, open setup-log.txt in the install folder and look for "Only what changed since the last build is built again".
