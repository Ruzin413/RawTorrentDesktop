using System.Web;

namespace TorServices.Parser;

public class MagnetData
{
    public byte[] InfoHash { get; set; } = Array.Empty<byte>();
    public string? DisplayName { get; set; }
    public List<string> Trackers { get; set; } = new();
}

public static class MagnetParser
{
    public static MagnetData Parse(string magnet)
    {
        var uri = new Uri(magnet.Replace("magnet:?", "http://localhost/?"));

        var query = HttpUtility.ParseQueryString(uri.Query);

        string? xt = query["xt"];
        string? dn = query["dn"];
        string[] tr = query.GetValues("tr") ?? Array.Empty<string>();

        // xt = urn:btih:HASH
        string hashHex = xt?.Replace("urn:btih:", "") ?? "";

        return new MagnetData
        {
            InfoHash = HexToBytes(hashHex),
            DisplayName = dn,
            Trackers = tr.ToList()
        };
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];

        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

        return bytes;
    }
}