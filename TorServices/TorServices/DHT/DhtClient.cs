using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using TorServices.Parser;

namespace TorServices.DHT;

public class DhtClient : IDisposable
{
    private readonly UdpClient _udp = null!;
    private readonly DhtNodeId _localId;
    private readonly RoutingTable _routingTable;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pendingQueries = new();
    private bool _running = true;

    private readonly List<DhtNode> _bootstrapNodes = new()
    {
        new DhtNode("router.bittorrent.com", 6881),
        new DhtNode("dht.transmissionbt.com", 6881),
        new DhtNode("router.utorrent.com", 6881)
    };

    public DhtClient(int port = 6881)
    {
        // Try to bind to the requested port, or find the next available one
        int retryCount = 0;
        bool bound = false;
        while (retryCount < 10)
        {
            try
            {
                _udp = new UdpClient(port + retryCount);
                bound = true;
                break; 
            }
            catch (SocketException)
            {
                retryCount++;
                if (retryCount >= 10) 
                {
                    // Bind failure handled silently
                }
            }
        }

        _localId = DhtNodeId.Generate();
        _routingTable = new RoutingTable(_localId);

        if (bound)
        {
            Task.Run(ReceiveLoop);
            Task.Run(RefreshLoop);
        }
    }

    private async Task ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                var parser = new BencodeParser(result.Buffer);
                var parsed = parser.Parse();
                if (parsed is Dictionary<string, object> dict)
                {
                    HandleMessage(dict, result.RemoteEndPoint);
                }
            }
            catch { /* Ignore errors in receive loop */ }
        }
    }

    private void HandleMessage(Dictionary<string, object> msg, IPEndPoint remote)
    {
        if (!msg.ContainsKey("y")) return;
        string type = Encoding.ASCII.GetString(msg["y"] as byte[] ?? Array.Empty<byte>());

        if (type == "q") HandleQuery(msg, remote);
        else if (type == "r") HandleResponse(msg, remote);
    }

    private void HandleQuery(Dictionary<string, object> msg, IPEndPoint remote)
    {
        if (!msg.ContainsKey("q") || !msg.ContainsKey("t")) return;
        string q = Encoding.ASCII.GetString(msg["q"] as byte[] ?? Array.Empty<byte>());
        byte[] t = msg["t"] as byte[] ?? Array.Empty<byte>();

        if (q == "ping")
        {
            var response = new Dictionary<string, object>
            {
                { "t", t },
                { "y", "r" },
                { "r", new Dictionary<string, object> { { "id", _localId.Data } } }
            };
            Send(BencodeEncoder.EncodeDictionary(response), remote);
        }
    }

    private void HandleResponse(Dictionary<string, object> msg, IPEndPoint remote)
    {
        if (!msg.ContainsKey("t")) return;
        string tStr = Encoding.ASCII.GetString(msg["t"] as byte[] ?? Array.Empty<byte>());
        
        if (_pendingQueries.TryRemove(tStr, out var tcs))
        {
            tcs.TrySetResult(msg);
        }

        // Add node to routing table if possible
        if (msg.ContainsKey("r") && msg["r"] is Dictionary<string, object> r && r.ContainsKey("id"))
        {
            byte[]? idBytes = r["id"] as byte[];
            if (idBytes != null && idBytes.Length == 20)
            {
                _routingTable.AddNode(new DhtNode(remote.Address.ToString(), remote.Port, new DhtNodeId(idBytes)));
            }
        }
    }

    public async Task<List<string>> GetPeersAsync(byte[] infoHash)
    {
        // 1. Bootstrap if routing table is empty
        if (_routingTable.TotalNodes == 0)
        {
            var bootstrapTasks = _bootstrapNodes.Select(node => QueryFindNode(node, _localId)).ToList();
            await Task.WhenAny(Task.WhenAll(bootstrapTasks), Task.Delay(3000));
        }

        // 2. Iterative search
        var targetId = new DhtNodeId(infoHash);
        var peers = new ConcurrentBag<string>();
        var queried = new ConcurrentDictionary<string, bool>();
        
        for (int i = 0; i < 3; i++) // Max 3 iterations
        {
            var nodes = _routingTable.GetClosestNodes(targetId, 8);
            var tasks = new List<Task>();

            foreach (var node in nodes)
            {
                string key = $"{node.Ip}:{node.Port}";
                if (!queried.TryAdd(key, true)) continue;

                tasks.Add(Task.Run(async () =>
                {
                    var result = await QueryGetPeers(node, infoHash);
                    if (result != null)
                    {
                        foreach (var p in result) peers.Add(p);
                    }
                }));
            }

            if (tasks.Count == 0) break;
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(2000));
            if (peers.Count > 50) break;
        }

        return peers.Distinct().ToList();
    }

    private async Task QueryFindNode(DhtNode node, DhtNodeId target)
    {
        string tStr = Guid.NewGuid().ToString("N").Substring(0, 4);
        var query = new Dictionary<string, object>
        {
            { "t", Encoding.ASCII.GetBytes(tStr) },
            { "y", "q" },
            { "q", "find_node" },
            { "a", new Dictionary<string, object> 
                { 
                    { "id", _localId.Data },
                    { "target", target.Data }
                } 
            }
        };

        await QueryAsync(query, node, tStr);
    }

    private async Task<List<string>?> QueryGetPeers(DhtNode node, byte[] infoHash)
    {
        string tStr = Guid.NewGuid().ToString("N").Substring(0, 4);
        var query = new Dictionary<string, object>
        {
            { "t", Encoding.ASCII.GetBytes(tStr) },
            { "y", "q" },
            { "q", "get_peers" },
            { "a", new Dictionary<string, object> 
                { 
                    { "id", _localId.Data },
                    { "info_hash", infoHash }
                } 
            }
        };

        var response = await QueryAsync(query, node, tStr);
        if (response == null || !response.ContainsKey("r")) return null;

        var r = response["r"] as Dictionary<string, object>;
        if (r == null) return null;

        var foundPeers = new List<string>();
        if (r.ContainsKey("values"))
        {
            var values = r["values"] as List<object>;
            if (values != null)
            {
                foreach (var v in values)
                {
                    if (v is byte[] b && b.Length == 6)
                    {
                        foundPeers.Add($"{b[0]}.{b[1]}.{b[2]}.{b[3]}:{(b[4] << 8) | b[5]}");
                    }
                }
            }
        }

        if (r.ContainsKey("nodes"))
        {
            byte[]? nodes = r["nodes"] as byte[];
            if (nodes != null) ParseNodes(nodes);
        }

        return foundPeers;
    }

    private void ParseNodes(byte[] nodes)
    {
        for (int i = 0; i + 26 <= nodes.Length; i += 26)
        {
            byte[] id = new byte[20];
            Buffer.BlockCopy(nodes, i, id, 0, 20);
            string ip = $"{nodes[i + 20]}.{nodes[i + 21]}.{nodes[i + 22]}.{nodes[i + 23]}";
            int port = (nodes[i + 24] << 8) | nodes[i + 25];
            _routingTable.AddNode(new DhtNode(ip, port, new DhtNodeId(id)));
        }
    }

    private async Task<Dictionary<string, object>?> QueryAsync(Dictionary<string, object> query, DhtNode node, string tStr)
    {
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        _pendingQueries[tStr] = tcs;

        try
        {
            IPAddress? ip;
            if (!IPAddress.TryParse(node.Ip, out ip))
            {
                var addresses = await Dns.GetHostAddressesAsync(node.Ip);
                ip = addresses.FirstOrDefault();
                if (ip == null) throw new Exception("Could not resolve host");
            }

            var remote = new IPEndPoint(ip, node.Port);
            byte[] data = BencodeEncoder.EncodeDictionary(query);
            await _udp.SendAsync(data, data.Length, remote);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch
        {
            _pendingQueries.TryRemove(tStr, out _);
            return null;
        }
    }

    private void Send(byte[] data, IPEndPoint remote)
    {
        try { _udp.Send(data, data.Length, remote); } catch { }
    }

    private async Task RefreshLoop()
    {
        while (_running)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2));
                var target = _routingTable.TotalNodes > 0 
                  ? _routingTable.GetClosestNodes(DhtNodeId.Generate(), 1).FirstOrDefault() 
                  : _bootstrapNodes[0];
                
                await QueryFindNode(target ?? _bootstrapNodes[0], DhtNodeId.Generate());
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _running = false;
        _udp?.Dispose();
    }
}