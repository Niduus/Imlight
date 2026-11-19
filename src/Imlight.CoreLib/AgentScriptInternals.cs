using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Imlight.AgentScript")]

// The bots (bots/Imlight.Bots.csproj): built apart from the server, and by the running server itself for a bot update,
// so a bot update needs no new Ahziael (BotDoor). They use the server's own inner parts as they always did.
[assembly: InternalsVisibleTo("Imlight.Bots")]
