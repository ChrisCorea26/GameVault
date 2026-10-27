using GameVault.Relay;

// GameVault's netplay relay. Run it on a server both players can reach, open its UDP port, and enter
// "server:port" in each player's Settings → Netplay → Relay server.
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : NetplayRelay.DefaultPort;
using var relay = new NetplayRelay(port);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
relay.Paired += room => Console.WriteLine($"{DateTime.Now:T} Room {room:X8}: two players connected.");
Console.WriteLine($"GameVault netplay relay listening on UDP port {relay.Port}. Press Ctrl+C to stop.");
await relay.RunAsync(stop.Token);
