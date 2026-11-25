Fixed: Bots and creatures misjudged hits that only do extra damage under a condition
Some cards, like the Helephant treasure card, do part of their damage only when a condition holds. The bots and the creature AI counted that extra part every time, so they thought these cards would hit far harder than they really do. Ultimus thought Helephant would finish Karolak Nightspinner, but it hit for 510. Now the extra part counts only when its condition really holds on that target, the same way the server works out the hit.
Where: every battle, for bots and creatures choosing their hits.
Test: Give a bot a Helephant treasure card against a boss; its log line no longer says the hit finishes the boss when the boss has far more health than the hit does.
