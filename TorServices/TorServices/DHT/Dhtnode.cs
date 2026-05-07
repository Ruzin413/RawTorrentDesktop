namespace TorServices.DHT;

public class DhtNode
{
    public DhtNodeId? Id { get; set; }
    public string Ip { get; set; }
    public int Port { get; set; }
    public DateTime LastSeen { get; set; }

    public DhtNode(string ip, int port, DhtNodeId? id = null)
    {
        Ip = ip;
        Port = port;
        Id = id;
        LastSeen = DateTime.UtcNow;
    }

    public DhtNode(string address, DhtNodeId? id = null)
    {
        var parts = address.Split(':');
        Ip = parts[0];
        Port = int.Parse(parts[1]);
        Id = id;
        LastSeen = DateTime.UtcNow;
    }
}