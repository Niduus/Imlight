Fixed: Ultimus going back to a side trip he had already dropped
When Ultimus dropped a shopping trip because he got stuck on the way, the next part of the same trip, through another area, looked like a new trip, so he set off again. This happened four times with the Ring of the Black Mantle. A dropped trip is now left alone in every part for 3 hours, and so is a trip that ran over its time budget.
Where: Ultimus's Thoughts and log ("SIDE TRIP DROPPED" and "TIME BUDGET" lines), and Guard.TripOutOfReachHours in his playbook.
Test: after a "SIDE TRIP DROPPED" line for a shopping trip, check that the same item's trip does not start again for 3 hours.
