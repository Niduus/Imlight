Fixed: Ultimus kept talking to someone with the right name but the wrong quest
When a "talk to" step's person shares a name with someone else, Ultimus and the Quest Team sometimes stood by the other person and kept clicking their quest chat, which never finishes the step. Now, if a same-name person only offers their own quest chat and nothing for the step, they are treated as the wrong person, and the bots go looking for the right one.
Where: any "talk to" step where two people share a name, such as Watson in Marleybone.
Test: on a step whose person shares a name, check that Ultimus stops at the wrong one only a few seconds before going to find the right one.
