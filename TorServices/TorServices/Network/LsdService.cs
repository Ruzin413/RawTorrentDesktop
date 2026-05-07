using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TorServices.Network;

public class LsdService
{
    private const string LSD_MULTICAST_IP = "239.192.152.143";
    private const int LSD_PORT = 6771;
    private readonly UdpClient _udp;
    private readonly Action<byte[], string> _onPeerFound;
    private bool _running = false;

    public LsdService(Action<byte[], string> onPeerFound)
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, LSD_PORT));
        _udp.JoinMulticastGroup(IPAddress.Parse(LSD_MULTICAST_IP));
        _onPeerFound = onPeerFound;
    }

    public void Start()
    {
        _running = true;
        _ = ListenLoop();
    }

    public async Task Announce(byte[] infoHash, int port)
    {
        string hexHash = BitConverter.ToString(infoHash).Replace("-", "").ToLower();
        string message = $"BT-SEARCH * HTTP/1.1\r\n" +
                         $"Host: {LSD_MULTICAST_IP}:{LSD_PORT}\r\n" +
                         $"Port: {port}\r\n" +
                         $"Infohash: {hexHash}\r\n" +
                         $"\r\n\r\n";

        byte[] data = Encoding.UTF8.GetBytes(message);
        await _udp.SendAsync(data, data.Length, LSD_MULTICAST_IP, LSD_PORT);
    }

    private async Task ListenLoop()
    {
        while (_running)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                string message = Encoding.UTF8.GetString(result.Buffer);
                
                if (message.StartsWith("BT-SEARCH"))
                {
                    var lines = message.Split("\r\n");
                    string? hexHash = lines.FirstOrDefault(l => l.StartsWith("Infohash:"))?.Split(":")[1].Trim();
                    string? portStr = lines.FirstOrDefault(l => l.StartsWith("Port:"))?.Split(":")[1].Trim();

                    if (hexHash != null && portStr != null)
                    {
                        byte[] hash = StringToByteArray(hexHash);
                        string peerAddr = $"{result.RemoteEndPoint.Address}:{portStr}";
                        _onPeerFound(hash, peerAddr);
                    }
                }
            }
            catch { if (!_running) break; }
        }
    }

    private static byte[] StringToByteArray(string hex)
    {
        return Enumerable.Range(0, hex.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                         .ToArray();
    }

    public void Stop()
    {
        _running = false;
        _udp.Close();
    }
}
