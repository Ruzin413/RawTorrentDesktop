namespace TorServices.Network;

public static class PeerMessage
{
    public const byte Choke = 0;
    public const byte Unchoke = 1;
    public const byte Interested = 2;
    public const byte NotInterested = 3;
    public const byte Have = 4;
    public const byte Bitfield = 5;
    public const byte Request = 6;
    public const byte Piece = 7;
    public const byte Extended = 20;
}