using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TorServices.Models;

namespace TorServices.Data;

/// <summary>
/// Simple CSV-based persistence for a desktop torrent client.
/// Stores torrents.csv and progress.csv in the app data folder.
/// </summary>
public class CsvDataStore
{
    private readonly string _dataDir;
    private readonly string _torrentsFile;
    private readonly string _progressFile;
    private readonly object _fileLock = new();

    public CsvDataStore()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RawTorrent");
        Directory.CreateDirectory(_dataDir);

        _torrentsFile = Path.Combine(_dataDir, "torrents.csv");
        _progressFile = Path.Combine(_dataDir, "progress.csv");

        EnsureFiles();
    }

    private void EnsureFiles()
    {
        if (!File.Exists(_torrentsFile))
            File.WriteAllText(_torrentsFile, "Id,Name,OutputDir,MagnetUri,TorrentPath,CreatedAt,ClientId\n");
        if (!File.Exists(_progressFile))
            File.WriteAllText(_progressFile, "TorrentId,Status,Progress,TotalPieces,CompletedPieces,Bitfield,LastUpdatedAt\n");
    }

    // --- Torrents ---

    public List<TorrentRecord> GetAllTorrents()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_torrentsFile)) return new List<TorrentRecord>();
            var lines = File.ReadAllLines(_torrentsFile).Skip(1); // skip header
            var records = new List<TorrentRecord>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var r = ParseTorrentLine(line);
                if (r != null) records.Add(r);
            }
            return records;
        }
    }

    public TorrentRecord? FindTorrent(string id)
    {
        return GetAllTorrents().FirstOrDefault(r => r.Id == id);
    }

    public void SaveTorrent(TorrentRecord record)
    {
        lock (_fileLock)
        {
            var all = GetAllTorrentsUnsafe();
            var idx = all.FindIndex(r => r.Id == record.Id);
            if (idx >= 0)
                all[idx] = record;
            else
                all.Add(record);
            WriteTorrents(all);
        }
    }

    public void RemoveTorrent(string id)
    {
        lock (_fileLock)
        {
            var all = GetAllTorrentsUnsafe();
            all.RemoveAll(r => r.Id == id);
            WriteTorrents(all);
        }
    }

    // --- Progress ---

    public List<TorrentProgress> GetAllProgress()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_progressFile)) return new List<TorrentProgress>();
            var lines = File.ReadAllLines(_progressFile).Skip(1);
            var records = new List<TorrentProgress>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = ParseProgressLine(line);
                if (p != null) records.Add(p);
            }
            return records;
        }
    }

    public TorrentProgress? FindProgress(string torrentId)
    {
        return GetAllProgress().FirstOrDefault(p => p.TorrentId == torrentId);
    }

    public void SaveProgress(TorrentProgress progress)
    {
        lock (_fileLock)
        {
            var all = GetAllProgressUnsafe();
            var idx = all.FindIndex(p => p.TorrentId == progress.TorrentId);
            if (idx >= 0)
                all[idx] = progress;
            else
                all.Add(progress);
            WriteProgress(all);
        }
    }

    public void RemoveProgress(string torrentId)
    {
        lock (_fileLock)
        {
            var all = GetAllProgressUnsafe();
            all.RemoveAll(p => p.TorrentId == torrentId);
            WriteProgress(all);
        }
    }

    public void ClearAll()
    {
        lock (_fileLock)
        {
            WriteTorrents(new List<TorrentRecord>());
            WriteProgress(new List<TorrentProgress>());
        }
    }
    private List<TorrentRecord> GetAllTorrentsUnsafe()
    {
        if (!File.Exists(_torrentsFile)) return new List<TorrentRecord>();
        var lines = File.ReadAllLines(_torrentsFile).Skip(1);
        var records = new List<TorrentRecord>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var r = ParseTorrentLine(line);
            if (r != null) records.Add(r);
        }
        return records;
    }

    private List<TorrentProgress> GetAllProgressUnsafe()
    {
        if (!File.Exists(_progressFile)) return new List<TorrentProgress>();
        var lines = File.ReadAllLines(_progressFile).Skip(1);
        var records = new List<TorrentProgress>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = ParseProgressLine(line);
            if (p != null) records.Add(p);
        }
        return records;
    }

    private void WriteTorrents(List<TorrentRecord> records)
    {
        var lines = new List<string> { "Id,Name,OutputDir,MagnetUri,TorrentPath,CreatedAt,ClientId" };
        foreach (var r in records)
        {
            lines.Add($"{Esc(r.Id)},{Esc(r.Name)},{Esc(r.OutputDir)},{Esc(r.MagnetUri)},{Esc(r.TorrentPath)},{r.CreatedAt:O},{Esc(r.ClientId)}");
        }
        File.WriteAllLines(_torrentsFile, lines);
    }

    private void WriteProgress(List<TorrentProgress> records)
    {
        var lines = new List<string> { "TorrentId,Status,Progress,TotalPieces,CompletedPieces,Bitfield,LastUpdatedAt" };
        foreach (var p in records)
        {
            var bitfieldStr = p.Bitfield != null ? Convert.ToBase64String(p.Bitfield) : "";
            lines.Add($"{Esc(p.TorrentId)},{Esc(p.Status)},{p.Progress},{p.TotalPieces},{p.CompletedPieces},{bitfieldStr},{p.LastUpdatedAt:O}");
        }
        File.WriteAllLines(_progressFile, lines);
    }

    // --- CSV parsing ---

    private static TorrentRecord? ParseTorrentLine(string line)
    {
        try
        {
            var parts = SplitCsv(line);
            if (parts.Length < 7) return null;
            return new TorrentRecord
            {
                Id = Unesc(parts[0]),
                Name = Unesc(parts[1]),
                OutputDir = Unesc(parts[2]),
                MagnetUri = Unesc(parts[3]),
                TorrentPath = Unesc(parts[4]),
                CreatedAt = DateTime.TryParse(parts[5], out var dt) ? dt : DateTime.UtcNow,
                ClientId = Unesc(parts[6])
            };
        }
        catch { return null; }
    }

    private static TorrentProgress? ParseProgressLine(string line)
    {
        try
        {
            var parts = SplitCsv(line);
            if (parts.Length < 7) return null;
            byte[]? bitfield = null;
            if (!string.IsNullOrEmpty(parts[5]))
                bitfield = Convert.FromBase64String(parts[5]);

            return new TorrentProgress
            {
                TorrentId = Unesc(parts[0]),
                Status = Unesc(parts[1]),
                Progress = double.TryParse(parts[2], out var prog) ? prog : 0,
                TotalPieces = int.TryParse(parts[3], out var tp) ? tp : 0,
                CompletedPieces = int.TryParse(parts[4], out var cp) ? cp : 0,
                Bitfield = bitfield,
                LastUpdatedAt = DateTime.TryParse(parts[6], out var dt) ? dt : DateTime.UtcNow,
            };
        }
        catch { return null; }
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string Unesc(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1].Replace("\"\"", "\"");
        return value;
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }
}
