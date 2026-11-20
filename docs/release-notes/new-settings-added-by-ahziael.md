Fixed: New settings missing until the server update finished

After Ahziael updated, the Kitharas Family page said Imlight.ini had no [Kitharas] section yet, and the Live patches switch said it was not a setting. New settings were only added to Imlight.ini when the server part of an update was installed, so they were missing while that part still waited. Ahziael now adds any setting it knows about that the file is missing, with its normal default, the moment a page reads the file. Settings already in the file are never changed.

Where: Ahziael, every page that reads or saves Imlight.ini (Kitharas Family, Live patches switch, and the rest).
Test: Open Bots, Kitharas Family and flip the main switch, then flip the Live patches switch at the top. Neither should show the red "not a setting" message.
