Fixed: Quest steps finishing as soon as you walk into a place
Some quest steps are meant to finish when you reach a certain spot inside a dungeon, but they finished the moment you arrived. In "Pick up the Spare" the grab the crystal step finished on reaching the top floor of the Crystal Storage instead of at the storage itself. Every step like this now waits until you reach its spot, and finishes at once if you are already standing there.
Where: every quest step that is set to a spot inside a zone.
Test: in "Pick up the Spare", reach the Crystal Storage top floor; the step stays until you walk up to the storage.
