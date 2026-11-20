Fixed: Bots put power pip jewels in past 100%

Bots added up their power pip chance from their gear only, so set bonuses, pet talents, the mount and their level were left out. The True Form bot reached 110% power pip, and the extra 10% did nothing. Now bots read their real power pip chance, and a jewel only counts the power pip or school accuracy still missing to reach 100%. The rest of the socket goes to the next best stat, like universal accuracy.

Where: True Form bot and every bot that picks jewels.
Test: Ask the True Form bot for a setup and open his Advanced Traits page. Power Pips should be 100% or a little under, never over, and his triangle jewel should be accuracy instead of power pip.
