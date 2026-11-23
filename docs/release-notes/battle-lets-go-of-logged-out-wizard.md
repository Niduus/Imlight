Fixed: A wizard who logged out mid-battle could come back and only pass

When a wizard's connection ended during a battle, the battle sometimes kept holding that old connection. The wizard came back to a fight where every card they picked counted as a pass, even after a teammate revived them. Now the battle lets go of a wizard whose connection has ended at the start of the next round, keeps their pips, and they can walk back onto the circle and play as normal.
Where: any battle, most often a bot that logged out for a bot update while a player was fighting beside it.
Test: start a fight beside a bot, log the bot out (or let a bot update go through), and check that its circle frees up at the next round instead of passing forever.
