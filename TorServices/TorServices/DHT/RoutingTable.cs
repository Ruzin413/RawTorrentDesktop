using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TorServices.DHT;

public class RoutingTable
{
    private const int K = 8;
    private readonly DhtNodeId _localId;
    private readonly List<List<DhtNode>> _buckets = new(160);

    public RoutingTable(DhtNodeId localId)
    {
        _localId = localId;
        for (int i = 0; i < 160; i++)
        {
            _buckets.Add(new List<DhtNode>());
        }
    }

    public void AddNode(DhtNode node)
    {
        if (node.Id == null || node.Id == _localId) return;

        int bucketIndex = _localId.GetDistanceRank(node.Id);
        if (bucketIndex < 0) return;

        lock (_buckets[bucketIndex])
        {
            var bucket = _buckets[bucketIndex];
            var existing = bucket.FirstOrDefault(n => n.Id == node.Id);

            if (existing != null)
            {
                // Update last seen
                existing.LastSeen = DateTime.UtcNow;
                // Move to end (most recently seen)
                bucket.Remove(existing);
                bucket.Add(existing);
            }
            else if (bucket.Count < K)
            {
                node.LastSeen = DateTime.UtcNow;
                bucket.Add(node);
            }
            else
            {
                // TODO: Ping oldest node to see if it's still alive
            }
        }
    }

    public List<DhtNode> GetClosestNodes(DhtNodeId target, int count = K)
    {
        var allNodes = new List<(DhtNode Node, BigInteger Distance)>();

        for (int i = 0; i < 160; i++)
        {
            lock (_buckets[i])
            {
                foreach (var node in _buckets[i])
                {
                    if (node.Id != null)
                        allNodes.Add((node, target.XOR(node.Id)));
                }
            }
        }

        return allNodes
            .OrderBy(n => n.Distance)
            .Take(count)
            .Select(n => n.Node)
            .ToList();
    }

    public int TotalNodes => _buckets.Sum(b => b.Count);
}
