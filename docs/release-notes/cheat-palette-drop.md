Fixed: Dropping a block from the block list into a cheat works
Dragging a block from the Cheat blocks list onto a cheat, such as "wizard is" onto "Drop If or Do blocks here", showed "Something went wrong: Specified cast is not valid" and added nothing. Ahziael was reading the dragged list entry as the block itself. Now the block is added where you drop it. Clicking a block in the list still adds it at the end, as before.
Where: Player Cheats page and boss Cheats, on a cheat's Edit blocks page.
Test: Open a cheat, drag "wizard is" from the list onto the cheat's blocks: it appears where you let go, with no red error.
