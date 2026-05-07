namespace TorServices.Network;

public class PieceRequest
{
    public int Index { get; set; }
    public int Begin { get; set; }
    public int Length { get; set; }
}