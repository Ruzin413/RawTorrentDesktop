using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TorServices.Network;

public class TrackerClient
{
    private readonly HttpClient _client = new();

    public async Task<List<string>> GetPeers(string announceUrl, byte[] infoHash, long size, string peerId)
    {
        if (announceUrl.StartsWith("udp://"))
        {
            return await GetUdpPeers(announceUrl, infoHash, size, peerId);
        }

        string url = $"{announceUrl}?info_hash={ToHex(infoHash)}&peer_id={peerId}&port=6881&uploaded=0&downloaded=0&left={size}&compact=1";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var data = await _client.GetByteArrayAsync(url, cts.Token);

            var parser = new TorServices.Parser.BencodeParser(data);
            var result = parser.Parse() as Dictionary<string, object>;

            if (result != null && result.ContainsKey("peers"))
            {
                return ParsePeers((byte[])result["peers"]);
            }
        }
        catch
        {
            // ignore failure
        }
        return new List<string>();
    }

    private async Task<List<string>> GetUdpPeers(string announceUrl, byte[] infoHash, long size, string peerId)
    {
        try
        {
            var uri = new Uri(announceUrl);
            using var udp = new UdpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Random rnd = new Random();
            int transactionId = rnd.Next();

            // 1. Connect Request
            byte[] connReq = new byte[16];
            WriteInt64(connReq, 0, 0x41727101980L);
            WriteInt32(connReq, 8, 0); // Action: Connect
            WriteInt32(connReq, 12, transactionId);

            await udp.SendAsync(connReq, connReq.Length, uri.Host, uri.Port);

            var connRes = await udp.ReceiveAsync(cts.Token);
            if (connRes.Buffer.Length < 16) return new List<string>();

            int action = ReadInt32(connRes.Buffer, 0);
            int rxTxId = ReadInt32(connRes.Buffer, 4);
            if (action != 0 || rxTxId != transactionId) return new List<string>();

            long connectionId = ReadInt64(connRes.Buffer, 8);

            // 2. Announce Request
            transactionId = rnd.Next();
            byte[] annReq = new byte[98];
            WriteInt64(annReq, 0, connectionId);
            WriteInt32(annReq, 8, 1); // Action: Announce
            WriteInt32(annReq, 12, transactionId);
            Buffer.BlockCopy(infoHash, 0, annReq, 16, 20);

            byte[] pidBytes = Encoding.ASCII.GetBytes(peerId.PadRight(20, ' '));
            Buffer.BlockCopy(pidBytes, 0, annReq, 36, 20);

            WriteInt64(annReq, 56, 0); // uploaded
            WriteInt64(annReq, 64, size); // left
            WriteInt64(annReq, 72, 0); // downloaded
            WriteInt32(annReq, 80, 2); // event (started)
            WriteInt32(annReq, 84, 0); // IP
            WriteInt32(annReq, 88, rnd.Next()); // key
            WriteInt32(annReq, 92, -1); // num want
            WriteInt16(annReq, 96, 6881); // port

            await udp.SendAsync(annReq, annReq.Length, uri.Host, uri.Port);

            var annRes = await udp.ReceiveAsync(cts.Token);
            if (annRes.Buffer.Length < 20) return new List<string>();

            action = ReadInt32(annRes.Buffer, 0);
            rxTxId = ReadInt32(annRes.Buffer, 4);

            if (action == 3 || action != 1 || rxTxId != transactionId) return new List<string>(); // Error or mismatch

            byte[] peerData = new byte[annRes.Buffer.Length - 20];
            Buffer.BlockCopy(annRes.Buffer, 20, peerData, 0, peerData.Length);

            return ParsePeers(peerData);
        }
        catch
        {
            return new List<string>();
        }
    }

    private void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private void WriteInt64(byte[] buffer, int offset, long value)
    {
        buffer[offset] = (byte)(value >> 56);
        buffer[offset + 1] = (byte)(value >> 48);
        buffer[offset + 2] = (byte)(value >> 40);
        buffer[offset + 3] = (byte)(value >> 32);
        buffer[offset + 4] = (byte)(value >> 24);
        buffer[offset + 5] = (byte)(value >> 16);
        buffer[offset + 6] = (byte)(value >> 8);
        buffer[offset + 7] = (byte)value;
    }

    private int ReadInt32(byte[] buffer, int offset)
    {
        return (buffer[offset] << 24) |
               (buffer[offset + 1] << 16) |
               (buffer[offset + 2] << 8) |
               buffer[offset + 3];
    }

    private long ReadInt64(byte[] buffer, int offset)
    {
        long high = (long)ReadInt32(buffer, offset) & 0xFFFFFFFFL;
        long low = (long)ReadInt32(buffer, offset + 4) & 0xFFFFFFFFL;
        return (high << 32) | low;
    }

    private string ToHex(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
            sb.Append('%').Append(b.ToString("X2"));
        return sb.ToString();
    }

    private List<string> ParsePeers(byte[] data)
    {
        List<string> peers = new();
        for (int i = 0; i + 5 < data.Length; i += 6)
        {
            string ip = $"{data[i]}.{data[i + 1]}.{data[i + 2]}.{data[i + 3]}";
            int port = (data[i + 4] << 8) | data[i + 5];
            peers.Add($"{ip}:{port}");
        }
        return peers;
    }
}