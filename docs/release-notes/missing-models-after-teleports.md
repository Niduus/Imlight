Fixed: Wizards no longer vanish after a teleport within the same area
A street teleporter (like the ones to Triton Lane or Cyclops Lane in Olde Town), recalling to your marker in the same area, or teleporting to someone close by played the teleport effect, which fades the wizard out, but never brought the wizard back. The wizard could stay invisible to themselves and others, and could not talk to people or start battles until they changed area. Now the wizard comes back as soon as they land.
Test: In Olde Town, step on the teleporter to Triton Lane. When you land, your wizard should be visible, and a friend standing there should see you too. Then talk to someone right away.

Fixed: People, monsters and objects no longer go missing when you teleport away and straight back
When something was taken out of your game (you teleported away from it or walked out of its view distance) and was sent again a split second later (you came straight back), the game was still letting go of the old copy and threw the new one away, so it stayed missing until you walked away and back. The server now waits that split second before sending it again.
Test: Take a street teleporter and the one straight back. Everything near where you started should still be there: people, monsters, signs and doors.

Changed: Teleporters within an area no longer use up spaces in the area
Each touch of a street teleporter kept one of the area's spaces for wizards for good. They are given back now, so a street that stays open for a long time keeps working the same.
