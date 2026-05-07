using System.Text;

namespace TorServices.Parser;

public static class BencodeEncoder
{
    public static byte[] Encode(object value)
    {
        if (value is Dictionary<string, object> dict) return EncodeDictionary(dict);
        if (value is List<object> list) return EncodeList(list);
        if (value is string str) return EncodeString(str);
        if (value is byte[] bytes) return EncodeBytes(bytes);
        if (value is int or long or uint or ulong) return EncodeInteger(Convert.ToInt64(value));

        throw new ArgumentException($"Unsupported type for Bencode encoding: {value.GetType()}");
    }

    public static byte[] EncodeDictionary(Dictionary<string, object> dict)
    {
        List<byte> bytes = new();
        bytes.Add((byte)'d');

        var sortedKeys = dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (var key in sortedKeys)
        {
            bytes.AddRange(EncodeString(key));
            bytes.AddRange(Encode(dict[key]));
        }

        bytes.Add((byte)'e');
        return bytes.ToArray();
    }

    private static byte[] EncodeList(List<object> list)
    {
        List<byte> bytes = new();
        bytes.Add((byte)'l');

        foreach (var item in list)
        {
            bytes.AddRange(Encode(item));
        }

        bytes.Add((byte)'e');
        return bytes.ToArray();
    }

    private static byte[] EncodeString(string str)
    {
        return EncodeBytes(Encoding.UTF8.GetBytes(str));
    }

    private static byte[] EncodeBytes(byte[] bytes)
    {
        string prefix = $"{bytes.Length}:";
        byte[] prefixBytes = Encoding.ASCII.GetBytes(prefix);

        byte[] result = new byte[prefixBytes.Length + bytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, result, 0, prefixBytes.Length);
        Buffer.BlockCopy(bytes, 0, result, prefixBytes.Length, bytes.Length);
        return result;
    }

    private static byte[] EncodeInteger(long n)
    {
        return Encoding.ASCII.GetBytes($"i{n}e");
    }
}
