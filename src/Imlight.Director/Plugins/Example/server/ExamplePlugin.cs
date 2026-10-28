// A plugin's code: any number of .cs files in its server folder. The server compiles them when it starts or when the
// plugin is turned on, so they can use anything in the server (Imlight.CoreLib).
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Plugins;

namespace ExamplePlugin;

// Every class like this is loaded when the plugin is turned on.
internal sealed class Example : IImlightPlugin {

    internal static PluginContext Context;

    public void Load(PluginContext context) {
        Context = context;
        context.Log("Example plugin is on. Try .hello in game.");
    }

}

// Chat commands are written the way the server's own are; every one in a plugin is added by itself.
internal sealed class HelloCommand : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("hello")]
    private void Hello() {
        var settings = Example.Context.Settings;
        InformSenderClient(settings.Text("Greeting", "Hello from a plugin!"), settings.On("Shout"));
    }

}
