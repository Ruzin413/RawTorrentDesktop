using System.Text;

namespace TorServices.Parser;

public class BencodeParser
{
    private readonly byte[] _data;
    private int _index;
    public int CurrentIndex => _index;

    // THIS IS CRITICAL FOR BITTORRENT
    public byte[] RawInfoBytes { get; private set; } = Array.Empty<byte>();

    private int _infoStartIndex;

    public BencodeParser(byte[] data)
    {
        _data = data;
        _index = 0;
    }

    public object Parse()
    {
        return ParseNext();
    }

    // ---------------- CORE DISPATCH ----------------

    private object ParseNext()
    {
        char prefix = (char)_data[_index];

        if (char.IsDigit(prefix))
            return ParseString();

        if (prefix == 'i')
            return ParseInteger();

        if (prefix == 'l')
            return ParseList();

        if (prefix == 'd')
            return ParseDictionary();

        throw new Exception($"Invalid bencode format at index {_index}");
    }

    // ---------------- STRING ----------------

    private byte[] ParseString()
    {
        int colon = Array.IndexOf(_data, (byte)':', _index);

        int len = int.Parse(
            Encoding.ASCII.GetString(_data, _index, colon - _index));

        _index = colon + 1;

        byte[] result = new byte[len];
        Buffer.BlockCopy(_data, _index, result, 0, len);

        _index += len;

        return result;
    }

    // ---------------- INTEGER ----------------

    private long ParseInteger()
    {
        _index++; // skip 'i'

        int end = Array.IndexOf(_data, (byte)'e', _index);

        string num = Encoding.ASCII.GetString(_data, _index, end - _index);

        _index = end + 1;

        return long.Parse(num);
    }

    // ---------------- LIST ----------------

    private List<object> ParseList()
    {
        _index++; // skip 'l'

        var list = new List<object>();

        while (_data[_index] != 'e')
        {
            list.Add(ParseNext());
        }

        _index++; // skip 'e'

        return list;
    }

    // ---------------- DICTIONARY ----------------

    private Dictionary<string, object> ParseDictionary()
    {
        _index++; // skip 'd'

        var dict = new Dictionary<string, object>();

        while (_data[_index] != 'e')
        {
            byte[] keyBytes = ParseString();
            string key = Encoding.ASCII.GetString(keyBytes);

            // CRITICAL FIX: detect "info" dictionary start
            if (key == "info")
            {
                _infoStartIndex = _index;
            }

            object value = ParseNext();

            dict[key] = value;

            // CRITICAL FIX: capture exact bencoded "info"
            if (key == "info")
            {
                int endIndex = _index;
                RawInfoBytes = new byte[endIndex - _infoStartIndex];

                Buffer.BlockCopy(
                    _data,
                    _infoStartIndex,
                    RawInfoBytes,
                    0,
                    RawInfoBytes.Length
                );
            }
        }

        _index++; // skip 'e'

        return dict;
    }
}