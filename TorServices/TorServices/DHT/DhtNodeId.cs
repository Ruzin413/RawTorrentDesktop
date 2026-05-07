using System;
using System.Security.Cryptography;
using System.Numerics;

namespace TorServices.DHT;

public record DhtNodeId : IComparable<DhtNodeId>
{
    public const int Size = 20;
    private readonly byte[] _data;

    public DhtNodeId(byte[] data)
    {
        if (data.Length != Size)
            throw new ArgumentException($"DhtNodeId must be {Size} bytes.");
        
        _data = (byte[])data.Clone();
    }

    public static DhtNodeId Generate()
    {
        byte[] data = new byte[Size];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(data);
        return new DhtNodeId(data);
    }

    public static DhtNodeId FromHex(string hex)
    {
        if (hex.Length != Size * 2)
            throw new ArgumentException($"Hex string for DhtNodeId must be {Size * 2} characters long.");

        byte[] data = new byte[Size];
        for (int i = 0; i < Size; i++)
        {
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return new DhtNodeId(data);
    }

    public byte[] Data => (byte[])_data.Clone();

    public string ToHex() => Convert.ToHexString(_data).ToLower();

    public override string ToString() => ToHex();

    public BigInteger XOR(DhtNodeId other)
    {
        byte[] result = new byte[Size + 1]; // Extra byte for BigInteger unsigned positive
        for (int i = 0; i < Size; i++)
        {
            result[Size - 1 - i] = (byte)(_data[i] ^ other._data[i]);
        }
        return new BigInteger(result);
    }

    public int CompareTo(DhtNodeId? other)
    {
        if (other == null) return 1;
        for (int i = 0; i < Size; i++)
        {
            if (_data[i] < other._data[i]) return -1;
            if (_data[i] > other._data[i]) return 1;
        }
        return 0;
    }

    public int GetDistanceRank(DhtNodeId other)
    {
        for (int i = 0; i < Size; i++)
        {
            byte diff = (byte)(_data[i] ^ other._data[i]);
            if (diff != 0)
            {
                // Find the first non-zero bit
                int bitIndex = 7;
                while ((diff & (1 << bitIndex)) == 0)
                    bitIndex--;
                
                return (Size - 1 - i) * 8 + bitIndex;
            }
        }
        return -1; // Same ID
    }
}
