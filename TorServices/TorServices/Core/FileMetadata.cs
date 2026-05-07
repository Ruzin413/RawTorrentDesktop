namespace TorServices.Core;

public class FileMetadata
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public long Offset { get; set; }
}
