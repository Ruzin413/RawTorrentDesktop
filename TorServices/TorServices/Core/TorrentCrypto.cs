using System.Security.Cryptography;

namespace TorServices.Core;

public static class TorrentCrypto
{
    public static byte[] ComputeInfoHash(byte[] infoBytes)
    {
        using var sha1 = SHA1.Create();
        return sha1.ComputeHash(infoBytes);
    }
}