using System;
using System.Collections;

namespace TorServices.Network;

public class Bitfield
{
    private readonly BitArray _bits;

    public Bitfield(int length)
    {
        _bits = new BitArray(length);
    }

    public Bitfield(byte[] data, int length)
    {
        _bits = new BitArray(data.Length * 8);
        for (int i = 0; i < data.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                if (((data[i] >> (7 - bit)) & 1) != 0)
                {
                    int index = i * 8 + bit;
                    if (index < length) _bits.Set(index, true);
                }
            }
        }
    }

    public bool HasPiece(int index) => index >= 0 && index < _bits.Length && _bits[index];

    public void SetPiece(int index, bool value)
    {
        if (index >= 0 && index < _bits.Length) _bits.Set(index, value);
    }

    public int Length => _bits.Length;
}
