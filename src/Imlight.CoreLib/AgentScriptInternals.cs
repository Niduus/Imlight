using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Imlight.AgentScript")]
// Plugins (Server/Plugins) are compiled under this name so they can use the server's own internal types.
[assembly: InternalsVisibleTo("Imlight.Plugin")]
// The plugin loader makes plugins and their hooks, which are internal.
[assembly: InternalsVisibleTo("Imlight.Director")]
