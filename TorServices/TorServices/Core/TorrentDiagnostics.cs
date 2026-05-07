namespace TorServices.Core;

public static class TorrentDiagnostics
{
    public static void PrintStatus(
        string announce,
        byte[] infoHash,
        List<string> peers,
        bool handshakeSuccess)
    {
        Console.WriteLine("\n==============================");
        Console.WriteLine("*** TORRENT SYSTEM STATUS CHECK ***");
        Console.WriteLine("==============================");

        Console.WriteLine($"\n[i] Tracker:");
        Console.WriteLine(!string.IsNullOrEmpty(announce)
            ? "[OK]"
            : "[FAIL]");

        Console.WriteLine($"\n[i] InfoHash:");
        Console.WriteLine(infoHash != null && infoHash.Length == 20
            ? "[OK] VALID (20 bytes)"
            : "[FAIL] INVALID");

        Console.WriteLine($"\n[i] Peers:");
        Console.WriteLine(peers != null && peers.Count > 0
            ? $"[OK] FOUND ({peers.Count})"
            : "[FAIL] NO PEERS");

        Console.WriteLine($"\n[i] Handshake:");
        Console.WriteLine(handshakeSuccess
            ? "[OK] CONNECTED"
            : "[FAIL] FAILED");

        Console.WriteLine("\n==============================");

        if (handshakeSuccess && peers != null && peers.Count > 0)
            Console.WriteLine("[***] SYSTEM STATUS: READY FOR DOWNLOAD");
        else
            Console.WriteLine("[!] SYSTEM STATUS: NOT READY");
    }
}