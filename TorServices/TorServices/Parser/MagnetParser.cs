using System.Web;
using System.Text;

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
        if (string.IsNullOrWhiteSpace(magnet)) throw new ArgumentException("Magnet URI is empty.");

        // Some magnets might have spaces or extra prefixes
        magnet = magnet.Trim();
        if (!magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid magnet URI format.");

        var uri = new Uri(magnet.Replace("magnet:?", "http://localhost/?", StringComparison.OrdinalIgnoreCase));
        var query = HttpUtility.ParseQueryString(uri.Query);

        string? xt = query["xt"];
        string? dn = query["dn"];
        string[] tr = query.GetValues("tr") ?? Array.Empty<string>();

        // xt = urn:btih:HASH
        if (string.IsNullOrEmpty(xt) || !xt.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Magnet URI missing info-hash (xt).");

        string hashPart = xt.Substring(9); // Skip "urn:btih:"
        byte[] infoHash;

        if (hashPart.Length == 40)
        {
            infoHash = HexToBytes(hashPart);
        }
        else if (hashPart.Length == 32)
        {
            infoHash = Base32ToBytes(hashPart);
        }
        else
        {
            throw new ArgumentException($"Unsupported info-hash length: {hashPart.Length}");
        }

        return new MagnetData
        {
            InfoHash = infoHash,
            DisplayName = dn,
            Trackers = tr.Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList()
        };
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("Invalid hex length.");
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static byte[] Base32ToBytes(string base32)
    {
        const string charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        base32 = base32.ToUpperInvariant();
        int outputLength = base32.Length * 5 / 8;
        byte[] bytes = new byte[outputLength];

        int bitCount = 0;
        int currentByte = 0;
        int buffer = 0;

        foreach (char c in base32)
        {
            int value = charset.IndexOf(c);
            if (value < 0) throw new ArgumentException($"Invalid Base32 character: {c}");

            buffer = (buffer << 5) | value;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bytes[currentByte++] = (byte)((buffer >> (bitCount - 8)) & 0xFF);
                bitCount -= 8;
            }
        }

        return bytes;
    }
}