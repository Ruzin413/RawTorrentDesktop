using System.IO;

namespace TorServices.Parser;

public static class TorrentFileReader
{
    public static byte[] Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(path);

        return File.ReadAllBytes(path);
    }
}