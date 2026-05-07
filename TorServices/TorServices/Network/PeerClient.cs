using System.Net.Sockets;
using System.Text;

namespace TorServices.Network;

public class PeerClient
{
    public async Task<(bool success, bool extensions)> HandshakeAsync(NetworkStream stream, byte[] infoHash, string peerId)
    {
        try
        {
            byte[] handshake = new byte[68];

            handshake[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);

            // BEP 10 Extension Protocol Enable
            handshake[25] |= 0x10;

            Buffer.BlockCopy(infoHash, 0, handshake, 28, 20);
            Encoding.ASCII.GetBytes(peerId).CopyTo(handshake, 48);

            await stream.WriteAsync(handshake);

            byte[] response = new byte[68];
            int read = 0;

            while (read < 68)
            {
                int r = await stream.ReadAsync(response, read, 68 - read);
                if (r == 0) return (false, false);
                read += r;
            }

            bool success = response[0] == 19;
            bool extensions = (response[25] & 0x10) != 0;

            return (success, extensions);
        }
        catch
        {
            return (false, false);
        }
    }

    public static async Task SendMessageAsync(NetworkStream stream, byte id, byte[] payload, CancellationToken token = default)
    {
        byte[] msg = new byte[5 + payload.Length];
        WriteInt(msg, 0, 1 + payload.Length);
        msg[4] = id;
        Buffer.BlockCopy(payload, 0, msg, 5, payload.Length);
        await stream.WriteAsync(msg, 0, msg.Length, token);
    }

    public static async Task<(byte id, byte[] payload)> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] lenBuf = new byte[4];
        int headerOffset = 0;
        while (headerOffset < 4)
        {
            int r = await stream.ReadAsync(lenBuf, headerOffset, 4 - headerOffset, token);
            if (r == 0) throw new Exception("Disconnected during header read");
            headerOffset += r;
        }

        int length = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        if (length == 0) return (99, Array.Empty<byte>()); // Keep-alive

        byte[] body = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int r = await stream.ReadAsync(body, offset, length - offset, token);
            if (r == 0) throw new Exception("Disconnected during body read");
            offset += r;
        }

        byte id = body[0];
        byte[] payload = new byte[length - 1];
        if (length > 1) Buffer.BlockCopy(body, 1, payload, 0, length - 1);
        return (id, payload);
    }

    public static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}