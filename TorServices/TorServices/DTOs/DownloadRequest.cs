namespace TorServices.DTOs;

public class DownloadRequest
{
    public string? Path { get; set; }
    public string? Magnet { get; set; }
    public string? OutputDir { get; set; }
    public string? ClientId { get; set; }
}
 