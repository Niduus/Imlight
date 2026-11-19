Fixed: .unmute can now lift your own mute
A muted hall monitor or admin could not unmute themselves, because the server blocked every line they typed, even the .unmute command, and .unmute only worked on a selected player. Now typing .unmute me, or .unmute with your own wizard or account name, lifts your own mute, and it gets through even while you are muted. A plain .unmute does the same when you are the one muted, and still unmutes the selected player otherwise. Players who are not allowed to use .unmute stay muted.
Where: Chat commands, no setting.
Test: Mute yourself with .mute 10m test, then type hello and see that you are muted. Type .unmute me and you should see "You are no longer muted", then type hello and it shows in chat.
