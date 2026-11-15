Fixed: Ultimus keeps the whole main story quest list
Ultimus now writes down every main story quest of every main world, in order: who gives it, where, what it needs first, what it opens, which quest ends the world, and every step. He builds this list when the server starts and can look up any quest in it. You can read it yourself in the quest-tree.txt file in the Ultimus logs folder.
Where: Ultimus, when the server starts.
Test: Start the server, then open Server\logs\ultimus\quest-tree.txt; the Marleybone part should say it ends with the Big Ben quest, and that quest should be marked as the last quest of the world.
