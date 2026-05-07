using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TorServices.Core;

public class PieceManager : IDisposable
{
    private readonly TorrentMetadata _metadata;
    private readonly string _outputDir;
    private readonly ConcurrentDictionary<int, bool> _claimed = new();
    private readonly ConcurrentDictionary<int, bool> _completed = new();
    private readonly Dictionary<string, FileStream> _fileHandles = new();
    private readonly object _lock = new();

    public PieceManager(TorrentMetadata metadata, string outputDir, byte[]? initialBitfield = null)
    {
        _metadata = metadata;
        _outputDir = string.IsNullOrWhiteSpace(outputDir) ? @"C:" : outputDir;
        
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

        EnsureDirectoriesAndFiles();
        OpenHandles();
    }


    private void EnsureDirectoriesAndFiles()
    {
        try
        {
            foreach (var file in _metadata.Files)
            {
                string fullPath = Path.Combine(_outputDir, file.Path);
                string? dir = Path.GetDirectoryName(fullPath);
                
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(fullPath))
                {
                    using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    fs.SetLength(file.Length);
                }

                else
                {
                    using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    if (fs.Length < file.Length)

                        fs.SetLength(file.Length);
                }
            }
        }
        catch
        {
            // Error handled silently
            throw;
        }
    }

    private void OpenHandles()
    {
        try
        {
            foreach (var file in _metadata.Files)
            {
                string fullPath = Path.Combine(_outputDir, file.Path);
                var fs = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                _fileHandles[file.Path] = fs;
            }
        }
        catch
        {
            // Error handled silently
            throw;
        }
    }

    public bool TryClaimPiece(int index)
    {
        if (_completed.ContainsKey(index)) return false;
        return _claimed.TryAdd(index, true);
    }

    public void ReleasePiece(int index)
    {
        _claimed.TryRemove(index, out _);
    }

    public bool IsClaimed(int index) => _claimed.ContainsKey(index);

    public void MarkCompleted(int index)
    {
        _completed.TryAdd(index, true);
        _claimed.TryRemove(index, out _);
    }

    public bool IsPieceCompleted(int index) => _completed.ContainsKey(index);

    public int CompletedCount => _completed.Count;

    public void Store(int index, byte[] data)
    {
        lock (_lock)
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
                    int bytesToWrite = (int)Math.Min(
                        file.Length - writeOffsetInFile,
                        bytesToProcess - readOffsetInData
                    );

                    if (_fileHandles.TryGetValue(file.Path, out var fs))
                    {
                        fs.Seek(writeOffsetInFile, SeekOrigin.Begin);
                        fs.Write(data, readOffsetInData, bytesToWrite);
                        fs.Flush(); // Ensure data is written to disk
                    }
                }
            }
            MarkCompleted(index);
        }
    }

    public void BuildFile(int totalPieces, string fileName)
    {
        // Progress logged to status property instead
    }

    public byte[] GetBitfield()
    {
        int totalPieces = _metadata.Pieces.Length / 20;
        byte[] bitfield = new byte[(totalPieces + 7) / 8];
        foreach (var index in _completed.Keys)
        {
            int byteIndex = index / 8;
            int bitIndex = 7 - (index % 8);
            if (byteIndex < bitfield.Length)
            {
                bitfield[byteIndex] |= (byte)(1 << bitIndex);
            }
        }
        return bitfield;
    }

    public void Dispose()

    {
        lock (_lock)
        {
            foreach (var handle in _fileHandles.Values)
            {
                handle.Dispose();
            }
            _fileHandles.Clear();
        }
    }
}