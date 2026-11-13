# Ultimus gets himself out of dead ends

Fixed: Ultimus stayed on the Kraken isle for hours on his way to Olde Town. The isle can only be left by its teleporter. Each time he stepped aside to free himself, he walked far enough away that the server forgot he had been stuck there, and then he walked straight back. So he never reached the third try, where the server sends him back to the start of the area.

Now the server remembers the places he was stuck in the last 20 minutes. Walking back to the same dead end carries on the count, so on the third try he is sent to where the area starts and finds his way to the next main story step from there. The Quest Team uses the same stuck check and gets the same fix.

Where: no settings. The Ultimus log says in pink "is still stuck ... goes back to where Triton Avenue starts".

Test:
1. Turn Ultimus on while he is on the Kraken isle.
2. Within about 5 minutes he should be sent back to the start of Triton Avenue and walk on toward Olde Town.
