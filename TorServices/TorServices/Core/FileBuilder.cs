using System.IO;

namespace TorServices.Core;

public class FileBuilder
{
    private List<byte> _file = new();

    public void AddPiece(byte[] piece)
    {
        _file.AddRange(piece);
    }

    public void Save(string path)
    {
        File.WriteAllBytes(path, _file.ToArray());
        // Success handled silently
    }
}