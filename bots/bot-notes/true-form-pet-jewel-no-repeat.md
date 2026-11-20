Fixed: True Form pet jewel repeated a talent he already had

The True Form bot could put a pet jewel on his pet that gave the same kind of stat as a talent the pet already had, like a +5 resist jewel next to a +10 resist talent. Now the jewel never gives anything his pet already has, whether the talent is showing or still in the pet's pool. It picks a different jewel that adds something new, like pierce or accuracy.

Where: True Form bot, pet jewel.
Test: Ask the True Form bot for any setup, then look at his pet card. The jewel's talent should not match any talent the pet shows.
