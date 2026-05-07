using System.Net.Sockets;
using System.Text;
using TorServices.Parser;

namespace TorServices.Network;

public class PieceDownloader
{
    private const int BlockSize = 16384;

    public async Task WaitForUnchokeAsync(NetworkStream stream, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var (id, payload) = await PeerClient.ReadMessageAsync(stream, token);
            
            if (id == PeerMessage.Unchoke)
                return;
                
            // Handle or skip messages that might arrive before unchoke
            // (Bitfield, Have, Extended, etc. are common right after handshake)
        }
    }

    public async Task<byte[]> DownloadPiece(NetworkStream stream, int index, int length, CancellationToken token)
    {
        byte[] data = new byte[length];
        int receivedBytes = 0;
        int requestedOffset = 0;
        int pendingRequests = 0;
        const int MaxPending = 8; // Standard BitTorrent pipeline size

        // 1. Send Extended Handshake (BEP 10)
        var hsDict = new Dictionary<string, object>
        {
            { "m", new Dictionary<string, object> { { "ut_metadata", 1 }, { "ut_pex", 1 } } }
        };
        byte[] extPayload = BencodeEncoder.EncodeDictionary(hsDict);
        byte[] extMsg = new byte[6 + extPayload.Length];
        PeerClient.WriteInt(extMsg, 0, 2 + extPayload.Length);
        extMsg[4] = PeerMessage.Extended;
        extMsg[5] = 0; // Subtype 0: Handshake
        Buffer.BlockCopy(extPayload, 0, extMsg, 6, extPayload.Length);
        await stream.WriteAsync(extMsg, 0, extMsg.Length, token);

        // 2. Standard Interested signal
        byte[] interestedMsg = { 0, 0, 0, 1, PeerMessage.Interested };
        await stream.WriteAsync(interestedMsg, 0, 5, token);

        await WaitForUnchokeAsync(stream, token);

        while (receivedBytes < length)
        {
            token.ThrowIfCancellationRequested();

            // 3. Keep pipeline full
            while (pendingRequests < MaxPending && requestedOffset < length)
            {
                int blockLen = Math.Min(BlockSize, length - requestedOffset);
                byte[] request = BuildRequest(index, requestedOffset, blockLen);
                await stream.WriteAsync(request, 0, request.Length, token);
                
                requestedOffset += blockLen;
                pendingRequests++;
            }

            // 4. Read response
            var (id, payload) = await PeerClient.ReadMessageAsync(stream, token);

            if (id == PeerMessage.Choke)
            {
                await WaitForUnchokeAsync(stream, token);
                requestedOffset = receivedBytes;
                pendingRequests = 0;
                continue;
            }

            if (id == PeerMessage.Piece)
            {
                if (payload.Length <= 8) continue;

                int pIndex = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
                int pBegin = (payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7];

                if (pIndex != index) continue;

                int dataLen = payload.Length - 8;
                if (dataLen <= 0 || pBegin + dataLen > length) continue;

                Buffer.BlockCopy(payload, 8, data, pBegin, dataLen);
                receivedBytes += dataLen;
                pendingRequests--;
            }
        }
        return data;
    }

    private byte[] BuildRequest(int index, int begin, int length)
    {
        byte[] msg = new byte[17];
        msg[3] = 13;
        msg[4] = PeerMessage.Request;

        PeerClient.WriteInt(msg, 5, index);
        PeerClient.WriteInt(msg, 9, begin);
        PeerClient.WriteInt(msg, 13, length);

        return msg;
    }
}