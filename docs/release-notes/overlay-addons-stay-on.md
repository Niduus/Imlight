Fixed: Overlay add-ons stay on after an update
The Minimap, Quest Tracker and anything else on your overlay could vanish after an update, so they had to be put back on each time. This happened when the overlay file couldn't be read for a moment (for example while the update was making its backup, or while the other copy of Ahziael was saving it): Ahziael then started from an empty overlay and could save that over yours. Now Ahziael tries again, keeps showing what you had, and uses the last good copy (overlay.json.last-good) if the file really can't be read. An unreadable file is kept aside instead of being saved over. A single piece from an older or newer Ahziael that can't be read is left out, not the whole overlay.
Where: Play > Overlay
Test: Put the Minimap and Quest Tracker on and install the next update: they're still there afterwards.

New: The add-ons list shows what's on your overlay
Under Your add-ons, each widget has a tick. Ticked (and marked "on") means it's on the overlay you're changing now. Tick one to put it on, untick it to take it off (Ahziael asks first). The ticks follow the Server and Client parts.
Where: Play > Overlay > Design it > Add-ons > Your add-ons
Test: Your Minimap and Quest Tracker show ticked. Untick the Minimap: it goes off the overlay. Tick it again: it comes back.
