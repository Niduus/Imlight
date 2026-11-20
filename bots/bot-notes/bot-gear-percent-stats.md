Fixed: Bots read percent stats on gear, jewels and mounts at their real size

Most percent stats, like school damage, every-school damage, pierce, power pip chance and healing, were read as a tiny share (0.15 instead of 15). So a +15% damage piece weighed less than a point of health, and a mount's damage read as 0, which is why True Form picked mounts for their looks. All bots now read these stats the way the game window shows them, so gear, jewels, pets and mounts are compared by what they really give.

Where: True Form's gear, jewels and mount, and the gear choices of Ultimus, the Quest Team and Bot Players.
Test: Call True Form and check his mount shows damage or another combat stat, and check Ultimus and the Quest Team still wear their best gear after the update.
