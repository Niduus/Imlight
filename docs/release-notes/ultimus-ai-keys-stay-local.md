Fixed: Ultimus's AI keys can never be uploaded with the project's code
Your online AI keys were already kept only on the PC that runs the server, in Server\Config\ultimus-ai-keys.json, and never written to Imlight.ini, the logs, a log zip, a backup or Remote Admin. The project's code folder now also refuses that file by name, so a key can never be uploaded with the code by mistake. A key is only ever sent to the AI service it belongs to, over a secure connection.
Where: Ahziael, the Ultimus AI Chat page, and the project's code folder.
Test: Save a key on the Ultimus AI Chat page, then make a log zip and a backup; neither holds the key, and Server\Config\ultimus-ai-keys.json is the only place it is.
