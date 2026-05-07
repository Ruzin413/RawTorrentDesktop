using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TorServices.Parser;
using TorServices.Core;

namespace TorServices.Network;

public class MetadataFetcher
{
    private const int MetadataPieceSize = 16384;

    public async Task<byte[]?> FetchMetadataAsync(string ip, int port, byte[] expectedInfoHash, string peerId, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        using var tcp = new TcpClient();
        
        try {
            await tcp.ConnectAsync(ip, port, cts.Token);
        } catch { return null; }

        var stream = tcp.GetStream();
        var peerClient = new PeerClient();

        var (hsOk, hsExtensions) = await peerClient.HandshakeAsync(stream, expectedInfoHash, peerId);
        if (!hsOk || !hsExtensions) return null;

        // 1. Send Extended Handshake
        int myMetadataId = 1;
        var hsDict = new Dictionary<string, object> {
            { "m", new Dictionary<string, object> { { "ut_metadata", myMetadataId } } }
        };
        await SendExtendedMessageAsync(stream, 0, BencodeEncoder.EncodeDictionary(hsDict), cts.Token);

        int metadataSize = -1;
        int utMetadataId = -1;

        // 2. Wait for peer extended handshake
        while (metadataSize == -1)
        {
            var (id, block) = await PeerClient.ReadMessageAsync(stream, cts.Token);
            if (id == PeerMessage.Extended && block.Length > 0 && block[0] == 0)
            {
                var dict = new BencodeParser(block.Skip(1).ToArray()).Parse() as Dictionary<string, object>;
                if (dict != null)
                {
                    if (dict.ContainsKey("metadata_size")) metadataSize = Convert.ToInt32(dict["metadata_size"]);
                    if (dict.ContainsKey("m") && dict["m"] is Dictionary<string, object> m && m.ContainsKey("ut_metadata"))
                        utMetadataId = Convert.ToInt32(m["ut_metadata"]);
                }
            }
        }

        if (metadataSize <= 0 || utMetadataId <= 0) return null;

        // 3. Download all pieces
        int totalPieces = (int)Math.Ceiling(metadataSize / (double)MetadataPieceSize);
        byte[] fullMetadata = new byte[metadataSize];
        int piecesDownloaded = 0;

        for (int i = 0; i < totalPieces; i++)
        {
            var reqDict = new Dictionary<string, object> { { "msg_type", 0 }, { "piece", i } };
            await SendExtendedMessageAsync(stream, utMetadataId, BencodeEncoder.EncodeDictionary(reqDict), cts.Token);

            bool pieceReceived = false;
            while (!pieceReceived)
            {
                var (id, block) = await PeerClient.ReadMessageAsync(stream, cts.Token);
                if (id == PeerMessage.Extended && block.Length > 0 && block[0] == myMetadataId)
                {
                    var parser = new BencodeParser(block.Skip(1).ToArray());
                    var dict = parser.Parse() as Dictionary<string, object>;
                    if (dict != null && dict.ContainsKey("msg_type") && Convert.ToInt32(dict["msg_type"]) == 1)
                    {
                        int pieceIdx = Convert.ToInt32(dict["piece"]);
                        if (pieceIdx == i)
                        {
                            int bencodeEnd = parser.CurrentIndex;
                            int dataLen = block.Length - 1 - bencodeEnd;
                            Buffer.BlockCopy(block, 1 + bencodeEnd, fullMetadata, i * MetadataPieceSize, dataLen);
                            pieceReceived = true;
                            piecesDownloaded++;
                        }
                    }
                }
            }
        }

        // 4. Verify SHA1 of metadata against expected info-hash
        using var sha1 = SHA1.Create();
        byte[] actualHash = sha1.ComputeHash(fullMetadata);
        
        for (int i = 0; i < 20; i++)
            if (actualHash[i] != expectedInfoHash[i]) return null; // Hash mismatch

        return fullMetadata;
    }

    private async Task SendExtendedMessageAsync(NetworkStream stream, int extId, byte[] payload, CancellationToken token)
    {
        byte[] msg = new byte[6 + payload.Length];
        PeerClient.WriteInt(msg, 0, 2 + payload.Length);
        msg[4] = PeerMessage.Extended;
        msg[5] = (byte)extId;
        Buffer.BlockCopy(payload, 0, msg, 6, payload.Length);
        await stream.WriteAsync(msg, 0, msg.Length, token);
    }
}
