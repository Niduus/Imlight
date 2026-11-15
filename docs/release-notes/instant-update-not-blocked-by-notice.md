Fixed: Instant Update got stuck behind a Quest Team notice
When a critical Quest Team issue box was left open, Ahziael counted it as a question box and never restarted into a new version that was ready to install. Instant Update now closes that notice and goes ahead, since the issue is already on the Team Issues page. Only boxes that ask you something still make it wait, and each wait is now written to Ahziael's logs, so you can see why.
Where: Ahziael with Instant Update on.
Test: leave a critical Quest Team issue box open while a new version is ready, and check that Ahziael still restarts into it within 5 minutes.
