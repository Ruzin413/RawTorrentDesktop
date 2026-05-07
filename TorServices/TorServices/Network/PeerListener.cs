using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TorServices.Core;

namespace TorServices.Network;

public class PeerListener
{
    private readonly TcpListener _listener;
    private readonly Func<byte[], PeerSession, Task> _onNewConnection;
    private bool _running = false;

    public PeerListener(int port, Func<byte[], PeerSession, Task> onNewConnection)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _onNewConnection = onNewConnection;
    }

    public void Start()
    {
        _running = true;
        _listener.Start();
        _ = AcceptLoop();
        Console.WriteLine($"[Listener] Now listening for incoming peers on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
    }

    public void Stop()
    {
        _running = false;
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleIncomingConnection(client);
            }
            catch { if (!_running) break; }
        }
    }

    private async Task HandleIncomingConnection(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            
            // 1. Read Handshake
            byte[] handshake = new byte[68];
            int read = 0;
            while (read < 68)
            {
                int r = await stream.ReadAsync(handshake, read, 68 - read);
                if (r == 0) return;
                read += r;
            }

            if (handshake[0] != 19) return; // Not a BitTorrent handshake

            byte[] infoHash = new byte[20];
            Buffer.BlockCopy(handshake, 28, infoHash, 0, 20);

            // 2. Identify which torrent this peer wants and hand it off
            // We need to create a session from an existing TcpClient
            string address = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            
            // Note: PeerSession currently only supports connecting TO peers.
            // I will update it to allow wrapping an existing connection.
            var session = new PeerSession(client, infoHash, address);
            
            await _onNewConnection(infoHash, session);
        }
        catch { client.Dispose(); }
    }
}
