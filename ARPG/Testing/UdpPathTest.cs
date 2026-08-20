using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ARPG.Testing;

/// <summary>
/// Built-in UDP path tester for multiplayer troubleshooting — no netcat or PowerShell
/// needed. Proves whether raw UDP flows between two machines on the game's port,
/// independent of the game protocol:
///
///   HOST machine:    Scrollbound --udpecho [port]        (listens + echoes, prints senders)
///   JOINING machine: Scrollbound --udpping host [port]   (sends once a second, prints replies)
///
/// If --udpping shows replies, the network path is fine and the game will connect.
/// If not, something between the machines (host firewall, VPN permissions) drops UDP.
/// </summary>
public static class UdpPathTest
{
    public static int RunEcho(int port)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException e)
        {
            Console.WriteLine($"Could not listen on UDP {port}: {e.Message}");
            Console.WriteLine("Is the game (or another copy of this test) already hosting on that port?");
            return 1;
        }
        Console.WriteLine($"UDP echo listening on 0.0.0.0:{port} — run on the OTHER machine:");
        Console.WriteLine($"    --udpping <this machine's IP> {port}");
        Console.WriteLine("Every received packet is printed and echoed back. Ctrl+C to stop.");
        while (true)
        {
            IPEndPoint from = new(IPAddress.Any, 0);
            byte[] data;
            try { data = socket.Receive(ref from); }
            catch (SocketException) { continue; } // ICMP-unreachable blips on Windows
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {data.Length} bytes from {from} — echoed back");
            socket.Send(data, data.Length, from);
        }
    }

    public static int RunPing(string host, int port)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Connect(host, port);
        }
        catch (SocketException e)
        {
            Console.WriteLine($"Could not resolve/reach {host}: {e.Message}");
            return 1;
        }
        socket.Client.ReceiveTimeout = 1000;
        Console.WriteLine($"Sending UDP probes to {host}:{port} once a second (Ctrl+C to stop)...");
        Console.WriteLine("The other machine must be running --udpecho on that port.");
        int sent = 0, received = 0;
        while (true)
        {
            var payload = Encoding.ASCII.GetBytes($"scrollbound-udp-test {++sent}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            socket.Send(payload, payload.Length);
            try
            {
                IPEndPoint from = new(IPAddress.Any, 0);
                var reply = socket.Receive(ref from);
                received++;
                Console.WriteLine($"  reply {received}/{sent} from {from}: {Encoding.ASCII.GetString(reply)} " +
                                  $"({clock.ElapsedMilliseconds} ms)");
            }
            catch (SocketException)
            {
                Console.WriteLine($"  no reply to probe {sent} (1s timeout) — " +
                                  (sent >= 3 && received == 0
                                      ? "UDP is being dropped: check the HOST's firewall (Public profile!) and VPN incoming-connection permissions."
                                      : "waiting..."));
            }
            var wait = 1000 - (int)clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }
}
