using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TorServices.Parser;

namespace TorServices.Network;

public class ExtensionManager
{
    private readonly Dictionary<string, int> _remoteExtensions = new();
    private readonly Dictionary<int, string> _myExtensions = new()
    {
        { 1, "ut_metadata" },
        { 2, "ut_pex" }
    };

    public bool SupportsPex => _remoteExtensions.ContainsKey("ut_pex");

    public int GetExtensionId(string name)
    {
        return _remoteExtensions.TryGetValue(name, out int id) ? id : -1;
    }

    public static async Task SendHandshakeAsync(NetworkStream stream, Dictionary<int, string> extensions, int? metadataSize = null)
    {
        var mDict = new Dictionary<string, object>();
        foreach (var ext in extensions)
        {
            mDict[ext.Value] = ext.Key;
        }

        var hsDict = new Dictionary<string, object>
        {
            { "m", mDict }
        };

        if (metadataSize.HasValue)
        {
            hsDict["metadata_size"] = (long)metadataSize.Value;
        }

        byte[] payload = BencodeEncoder.EncodeDictionary(hsDict);
        byte[] extMsg = new byte[1 + payload.Length];
        extMsg[0] = 0; // Subtype 0: Handshake
        Buffer.BlockCopy(payload, 0, extMsg, 1, payload.Length);

        await PeerClient.SendMessageAsync(stream, PeerMessage.Extended, extMsg);
    }

    public void HandleHandshake(byte[] payload)
    {
        var parser = new BencodeParser(payload);
        if (parser.Parse() is Dictionary<string, object> dict && dict.ContainsKey("m"))
        {
            if (dict["m"] is Dictionary<string, object> m)
            {
                foreach (var kv in m)
                {
                    _remoteExtensions[kv.Key] = Convert.ToInt32(kv.Value);
                }
            }
        }
    }

    public List<string> ParsePexMessage(byte[] payload)
    {
        var peers = new List<string>();
        var parser = new BencodeParser(payload);
        
        if (parser.Parse() is Dictionary<string, object> dict && dict.ContainsKey("added"))
        {
            if (dict["added"] is byte[] added)
            {
                for (int i = 0; i + 6 <= added.Length; i += 6)
                {
                    string ip = $"{added[i]}.{added[i+1]}.{added[i+2]}.{added[i+3]}";
                    int port = (added[i+4] << 8) | added[i+5];
                    peers.Add($"{ip}:{port}");
                }
            }
        }
        return peers;
    }

    public static async Task SendPexQueryAsync(NetworkStream stream, int pexId)
    {
        // Simple PEX "added" message (usually sent periodically to share peers)
        // For now, only handle incoming PEX from others
    }
}
