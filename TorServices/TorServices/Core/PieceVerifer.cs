using System.Security.Cryptography;

namespace TorServices.Core;

public static class PieceVerifier
{
    public static bool Verify(byte[] pieceData, byte[] expectedHash)
    {
        using var sha1 = SHA1.Create();

        byte[] hash = sha1.ComputeHash(pieceData);

        return hash.SequenceEqual(expectedHash);
    }
}
