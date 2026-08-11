using ARPG.Testing;

namespace ARPG;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--nettest"))
        {
            // Headless multiplayer self-test: runs a server plus two clients in-process
            // without any graphics. Used to validate the authoritative networking model.
            Environment.Exit(HeadlessNetTest.Run());
            return;
        }

        using var game = new GameMain
        {
            // Dev conveniences: skip the menu (used by automated smoke tests too).
            AutoSinglePlayer = args.Contains("--sp"),
        };
        game.Run();
    }
}
