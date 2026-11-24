Fixed: Arriving in an area no longer restarts your connection
Sometimes, right as a wizard arrived in a new area, the server was still changing that wizard's effects and the check for a Beastmoon form tripped over it. The server then restarted that wizard's services, which could drop or freeze them. The server now reads a copy of the effects, so this can't happen.
Where: arriving in any area.
Test: walk through doors and sigils between areas a few times; the server log shows no "Error while attaching effects".
