Fixed: Ultimus no longer walks into the middle of Big Ben's tower under Sprockets
On 0.10.888, after the last lever, Ultimus walked straight from the floor he was on into the middle of the tower and ended up two floors under Sprockets, so no battle could start. Now he only walks onto the raised counterweight from the top floor, next to it, and climbs the ramps first. A walk he remembers is also no longer reused when his step is on another floor, because a remembered walk does not know about heights.
Where: Ultimus, the Quest Team and Bot Players, in areas with several floors or a lift.
Test: Take Ultimus through "Stolen Away" in Counterweight East; after the fourth lever he should climb to the top floor, then walk onto the counterweight to Sprockets and start the battle.
