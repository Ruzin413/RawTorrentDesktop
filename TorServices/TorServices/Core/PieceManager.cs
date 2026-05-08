using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;

namespace TorServices.Core;

public class PieceManager : IDisposable
{
    private readonly TorrentMetadata _metadata;
    private readonly string _outputDir;
    private readonly ConcurrentDictionary<int, bool> _claimed = new();
    private readonly ConcurrentDictionary<int, bool> _completed = new();
    private readonly Dictionary<string, MemoryMappedFile> _mmFiles = new();
    private readonly object _lock = new();

    public PieceManager(TorrentMetadata metadata, string outputDir, byte[]? initialBitfield = null)
    {
        _metadata = metadata;
        _outputDir = string.IsNullOrWhiteSpace(outputDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TorrentDownload") : outputDir;
        
        if (initialBitfield != null)
        {
            for (int i = 0; i < _metadata.Pieces.Length / 20; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                if (byteIndex < initialBitfield.Length && (initialBitfield[byteIndex] & (1 << bitIndex)) != 0)
                {
                    _completed.TryAdd(i, true);
                }
            }
        }

        InitializeFiles();
    }

    private void InitializeFiles()
    {
        foreach (var file in _metadata.Files)
        {
            string fullPath = Path.Combine(_outputDir, file.Path);
            string? dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Open or create the file with the correct size
            var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length < file.Length) fs.SetLength(file.Length);
            fs.Dispose();

            // Map the file into memory
            var mmf = MemoryMappedFile.CreateFromFile(fullPath, FileMode.Open, null, file.Length, MemoryMappedFileAccess.ReadWrite);
            _mmFiles[file.Path] = mmf;
        }
    }

    public bool TryClaimPiece(int index) => !_completed.ContainsKey(index) && _claimed.TryAdd(index, true);
    public void ReleasePiece(int index) => _claimed.TryRemove(index, out _);
    public bool IsPieceCompleted(int index) => _completed.ContainsKey(index);
    public bool IsClaimed(int index) => _claimed.ContainsKey(index);
    public int CompletedCount => _completed.Count;

    public void Store(int index, byte[] data)
    {
        long pieceGlobalOffset = (long)index * _metadata.PieceLength;
        int bytesToProcess = data.Length;

        foreach (var file in _metadata.Files)
        {
            long fileEnd = file.Offset + file.Length;

            if (pieceGlobalOffset < fileEnd && pieceGlobalOffset + bytesToProcess > file.Offset)
            {
                long writeOffsetInFile = Math.Max(0, pieceGlobalOffset - file.Offset);
                int readOffsetInData = (int)Math.Max(0, file.Offset - pieceGlobalOffset);
                int bytesToWrite = (int)Math.Min(file.Length - writeOffsetInFile, bytesToProcess - readOffsetInData);

                if (_mmFiles.TryGetValue(file.Path, out var mmf))
                {
                    using var accessor = mmf.CreateViewAccessor(writeOffsetInFile, bytesToWrite);
                    accessor.WriteArray(0, data, readOffsetInData, bytesToWrite);
                }
            }
        }
        _completed.TryAdd(index, true);
        _claimed.TryRemove(index, out _);
    }

    public byte[] GetBitfield()
    {
        int totalPieces = _metadata.Pieces.Length / 20;
        byte[] bitfield = new byte[(totalPieces + 7) / 8];
        foreach (var index in _completed.Keys)
        {
            int byteIndex = index / 8;
            int bitIndex = 7 - (index % 8);
            if (byteIndex < bitfield.Length) bitfield[byteIndex] |= (byte)(1 << bitIndex);
        }
        return bitfield;
    }

    public void Dispose()
    {
        foreach (var mmf in _mmFiles.Values) mmf.Dispose();
        _mmFiles.Clear();
    }
}