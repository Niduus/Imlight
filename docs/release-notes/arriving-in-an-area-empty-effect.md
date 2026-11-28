Fixed: Arriving in an area no longer trips over an empty effect
A second way the same arrival problem showed up: when a wizard's effects were changed from two places at once, the list could keep an empty spot, and sending the effects to the game stopped at it and restarted the wizard's services. Empty spots are now skipped.
Where: arriving in any area.
Test: walk through doors and sigils between areas a few times; the server log shows no "Error while attaching effects".
