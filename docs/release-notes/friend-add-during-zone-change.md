Fixed: "Friends List Error. Cannot add friend." right after the other player changes zones
Adding a friend failed if the other player had just walked through a door, taken a teleport, or logged in a moment before, because the server briefly lost track of them while they loaded in. The server now waits a few seconds for them to arrive and remembers each player by their wizard, not only their account, so the add goes through.
Where: Friends list, Add Friend, and accepting a friend request.
Test: Ask a friend to go through a zone door, and send them a friend request right as they load in. The request reaches them and no error appears.
