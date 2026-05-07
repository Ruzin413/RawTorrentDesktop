using System;
using System.Security.Cryptography;

namespace TorServices.Core;

public static class PieceVerifier
{
    public static bool Verify(byte[] data, byte[] expectedHash)
    {
        // Use ReadOnlySpan to avoid unnecessary allocations
        ReadOnlySpan<byte> dataSpan = data;
        
        // Use the static HashData method for thread-safety and zero-allocation (on modern .NET)
        byte[] actualHash = SHA1.HashData(dataSpan);
        
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
