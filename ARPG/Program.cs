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

        // UDP path testers (multiplayer troubleshooting, console-only, cross-platform):
        //   --udpecho [port]        on the HOST machine
        //   --udpping <ip> [port]   on the JOINING machine
        // Replies flowing = the network path is fine; silence = firewall/VPN drops UDP.
        int argIndex = Array.IndexOf(args, "--udpecho");
        if (argIndex >= 0)
        {
            int port = argIndex + 1 < args.Length && int.TryParse(args[argIndex + 1], out int p)
                ? p : Core.GameNetConfig.DefaultPort;
            Environment.Exit(UdpPathTest.RunEcho(port));
            return;
        }
        argIndex = Array.IndexOf(args, "--udpping");
        if (argIndex >= 0)
        {
            if (argIndex + 1 >= args.Length)
            {
                Console.WriteLine("Usage: --udpping <host-ip> [port]");
                Environment.Exit(1);
                return;
            }
            string host = args[argIndex + 1];
            int port = argIndex + 2 < args.Length && int.TryParse(args[argIndex + 2], out int p)
                ? p : Core.GameNetConfig.DefaultPort;
            Environment.Exit(UdpPathTest.RunPing(host, port));
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
