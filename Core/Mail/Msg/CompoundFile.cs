using System.Text;

namespace CsAgent.Core.Mail.Msg;

// Pure C# reader for the Compound File Binary Format (CFBF, [MS-CFB]).
// NativeAOT friendly: no reflection, no dynamic code, no COM, no NuGet.

internal static class CfbConstants
{
    public const uint FreeSect = 0xFFFFFFFF;
    public const uint NoStream = 0xFFFFFFFF;
    public const uint EndOfChain = 0xFFFFFFFE;
    public const uint FatSect = 0xFFFFFFFD;
    public const uint DifSect = 0xFFFFFFFC;
}

internal sealed class DirectoryEntry
{
    public int Id;
    public string Name = "";
    public byte Type;
    public uint LeftSibling = CfbConstants.NoStream;
    public uint RightSibling = CfbConstants.NoStream;
    public uint Child = CfbConstants.NoStream;
    public uint StartSector = CfbConstants.EndOfChain;
    public ulong Size;
    public string Path = "";
    public ushort RawNameLength;
    // True when the entry was reached by walking the red/black tree from
    // the root. Unreached ("orphan") entries have no trustworthy path.
    public bool Reachable;
}

internal sealed class CompoundFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _br;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly ushort _majorVersion;
    private readonly uint _numDirectorySectors;
    private readonly uint _numFatSectors;
    private readonly uint[] _fat;
    private readonly long _maxStreamBytes;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly List<DirectoryEntry> _entries = new();
    private readonly Dictionary<string, DirectoryEntry> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private DirectoryEntry? _rootEntry;

    public DirectoryEntry Root => _rootEntry ?? throw new InvalidOperationException("CFBF root entry has not been initialized.");
    public IEnumerable<DirectoryEntry> Entries => _entries;

    internal IReadOnlyList<string> GetTopLevelStreamNames()
    {
        return _entries
            .Where(e => e.Type == 2 && e.Reachable && e.Path.IndexOf('\\') < 0)
            .Select(e => e.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CompoundFile(string path, long maxStreamBytes = 256L * 1024 * 1024)
    {
        if (maxStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxStreamBytes));
        _maxStreamBytes = maxStreamBytes;
        _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _br = new BinaryReader(_stream, Encoding.Unicode, leaveOpen: true);

        byte[] sig = ReadExactly(8);
        byte[] expected = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        if (!sig.SequenceEqual(expected))
            throw new InvalidDataException("Not a CFBF/Compound File.");

        _br.ReadBytes(16); // CLSID
        ushort minorVersion = _br.ReadUInt16();
        ushort majorVersion = _br.ReadUInt16();
        _majorVersion = majorVersion;
        ushort byteOrder = _br.ReadUInt16();
        ushort sectorShift = _br.ReadUInt16();
        ushort miniSectorShift = _br.ReadUInt16();
        _br.ReadBytes(6); // reserved

        if (byteOrder != 0xFFFE)
            throw new InvalidDataException("Unsupported CFBF byte order.");

        if (sectorShift != 9 && sectorShift != 12)
            throw new InvalidDataException($"Unsupported sector size shift: {sectorShift}.");

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniSectorShift;

        uint numDirectorySectors = _br.ReadUInt32();
        uint numFatSectors = _br.ReadUInt32();
        _numDirectorySectors = numDirectorySectors;
        _numFatSectors = numFatSectors;
        uint firstDirectorySector = _br.ReadUInt32();
        _br.ReadUInt32(); // transaction signature
        _miniStreamCutoff = _br.ReadUInt32();
        uint firstMiniFatSector = _br.ReadUInt32();
        uint numMiniFatSectors = _br.ReadUInt32();
        uint firstDifatSector = _br.ReadUInt32();
        uint numDifatSectors = _br.ReadUInt32();

        if (_miniSectorSize != 64 || _miniStreamCutoff != 4096)
            throw new InvalidDataException("Unsupported CFBF mini-stream parameters.");

        // Header DIFAT contains the first 109 FAT sector locations.
        var fatSectorIds = new List<uint>((int)Math.Min(numFatSectors, 109));
        for (int i = 0; i < 109; i++)
        {
            uint sector = _br.ReadUInt32();
            if (sector != CfbConstants.FreeSect)
                fatSectorIds.Add(sector);
        }

        // Additional DIFAT sectors are linked through the DIFAT itself.
        uint dif = firstDifatSector;
        for (uint i = 0; i < numDifatSectors && dif != CfbConstants.EndOfChain; i++)
        {
            byte[] block = ReadPhysicalSector(dif);
            using var dbr = new BinaryReader(new MemoryStream(block));
            int entriesPerDifatSector = _sectorSize / 4 - 1;
            for (int j = 0; j < entriesPerDifatSector; j++)
            {
                uint s = dbr.ReadUInt32();
                if (s != CfbConstants.FreeSect && fatSectorIds.Count < numFatSectors)
                    fatSectorIds.Add(s);
            }
            dif = dbr.ReadUInt32();
        }

        if (fatSectorIds.Count < numFatSectors)
            throw new InvalidDataException("Incomplete DIFAT/FAT.");

        _fat = ReadFat(fatSectorIds.Take((int)numFatSectors));

        byte[] directoryBytes = ReadFatStream(firstDirectorySector);
        ParseDirectory(directoryBytes);

        if (firstMiniFatSector != CfbConstants.EndOfChain && numMiniFatSectors > 0)
        {
            byte[] miniFatBytes = ReadFatStream(firstMiniFatSector);
            _miniFat = BytesToUInt32(miniFatBytes);
        }
        else
        {
            _miniFat = Array.Empty<uint>();
        }

        // Root directory entry contains the mini-stream itself.
        _miniStream = Root.StartSector == CfbConstants.EndOfChain || Root.Size == 0
            ? Array.Empty<byte>()
            : ReadFatStream(Root.StartSector, Root.Size);

        BuildPaths();
    }

    private uint[] ReadFat(IEnumerable<uint> fatSectorIds)
    {
        var result = new List<uint>();
        foreach (uint sector in fatSectorIds)
        {
            byte[] block = ReadPhysicalSector(sector);
            for (int p = 0; p + 4 <= block.Length; p += 4)
                result.Add(BitConverter.ToUInt32(block, p));
        }
        return result.ToArray();
    }

    private byte[] ReadFatStream(uint startSector, ulong requestedSize = ulong.MaxValue)
    {
        if (startSector == CfbConstants.EndOfChain ||
            startSector == CfbConstants.NoStream)
            return Array.Empty<byte>();

        var output = new MemoryStream();
        var seen = new HashSet<uint>();
        uint current = startSector;

        while (current != CfbConstants.EndOfChain)
        {
            if (current >= _fat.Length)
                throw new InvalidDataException($"FAT sector index out of range: {current}.");
            if (!seen.Add(current))
                throw new InvalidDataException("FAT chain contains a cycle.");
            if (current == CfbConstants.FreeSect || current == CfbConstants.FatSect ||
                current == CfbConstants.DifSect)
                throw new InvalidDataException("Invalid sector in FAT chain.");

            byte[] sector = ReadPhysicalSector(current);
            if (output.Length > _maxStreamBytes - sector.Length)
                throw new InvalidDataException("Stream exceeds configured maximum size.");
            output.Write(sector, 0, sector.Length);
            current = _fat[current];

            if ((ulong)output.Length >= requestedSize)
                break;
        }

        byte[] bytes = output.ToArray();
        if (requestedSize != ulong.MaxValue && requestedSize < (ulong)bytes.Length)
            Array.Resize(ref bytes, checked((int)requestedSize));
        return bytes;
    }

    private byte[] ReadMiniStream(uint startMiniSector, ulong size)
    {
        if (size == 0 || startMiniSector == CfbConstants.EndOfChain)
            return Array.Empty<byte>();

        if (_miniFat.Length == 0 || _miniStream.Length == 0)
            throw new InvalidDataException("Mini-FAT/mini-stream is missing.");

        using var output = new MemoryStream();
        var seen = new HashSet<uint>();
        uint current = startMiniSector;

        while (current != CfbConstants.EndOfChain)
        {
            if (current >= _miniFat.Length)
                throw new InvalidDataException($"Mini-FAT index out of range: {current}.");
            if (!seen.Add(current))
                throw new InvalidDataException("Mini-FAT chain contains a cycle.");

            long offset = checked((long)current * _miniSectorSize);
            if (offset < 0 || offset >= _miniStream.Length)
                throw new InvalidDataException("Mini-stream sector points outside mini-stream.");

            int count = Math.Min(_miniSectorSize, _miniStream.Length - (int)offset);
            if (output.Length > _maxStreamBytes - count)
                throw new InvalidDataException("Mini-stream exceeds configured maximum size.");
            output.Write(_miniStream, (int)offset, count);
            current = _miniFat[current];

            if ((ulong)output.Length >= size)
                break;
        }

        byte[] bytes = output.ToArray();
        if ((ulong)bytes.Length > size)
            Array.Resize(ref bytes, checked((int)size));
        return bytes;
    }

    public byte[] GetStream(string path)
    {
        string normalized = NormalizePath(path);
        DirectoryEntry? entry = null;

        // Normal lookup uses the fully-qualified CFBF path.
        _byPath.TryGetValue(normalized, out entry);

        // Outlook MSG files have several mandatory streams directly under
        // the root storage. Some producers create a valid directory tree
        // whose sibling links are unusual enough that a strict path walk
        // can miss an otherwise valid top-level stream. For a root-level
        // lookup, safely fall back to a unique stream with the same name.
        // Only consider entries the tree walk never reached: a reachable
        // stream with the same name belongs to a recipient/attachment and
        // must not be returned as if it were a root-level stream.
        if (entry == null && normalized.IndexOf('\\') < 0)
        {
            DirectoryEntry? candidate = null;
            foreach (var e in _entries)
            {
                if (e.Type != 2 || e.Reachable || !string.Equals(e.Name, normalized,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (candidate != null)
                {
                    candidate = null; // ambiguous: refuse to guess
                    break;
                }
                candidate = e;
            }
            entry = candidate;
        }

        if (entry == null)
        {
            var rootStreams = GetTopLevelStreamNames();
            int orphans = _entries.Count(e => e.Type is 1 or 2 && !e.Reachable);
            throw new FileNotFoundException(
                $"Stream '{path}' not found. Root contains {rootStreams.Count} stream(s)" +
                (rootStreams.Count > 0 ? ": " + string.Join(", ", rootStreams.Take(32)) : "") +
                $". Directory: {_entries.Count} entries, {orphans} unreachable from root. " +
                "Run with --dump to inspect the file structure.");
        }

        if (entry.Type != 2)
            throw new InvalidDataException($"'{path}' is not a stream.");

        if (entry.Size == 0)
            return Array.Empty<byte>();

        // CFBF uses the mini-FAT for streams strictly smaller than 4096 bytes.
        return entry.Size < _miniStreamCutoff
            ? ReadMiniStream(entry.StartSector, entry.Size)
            : ReadFatStream(entry.StartSector, entry.Size);
    }

    /// <summary>Size in bytes of a stream, or null when it does not exist.</summary>
    public long? GetStreamSize(string path) =>
        _byPath.TryGetValue(NormalizePath(path), out var e) && e.Type == 2 ? (long)e.Size : null;

    public bool HasStream(string path) =>
        _byPath.TryGetValue(NormalizePath(path), out var e) && e.Type == 2;

    /// <summary>Names of streams directly under a storage ("" = root).</summary>
    public IReadOnlyList<string> GetChildStreamNames(string storagePath, bool includeOrphans)
    {
        string parent = NormalizePath(storagePath);
        return _entries
            .Where(e => e.Type == 2 &&
                        ((e.Reachable && ParentPath(e.Path) == parent) ||
                         (includeOrphans && !e.Reachable)))
            .Select(e => e.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('\\');
        return i < 0 ? "" : path[..i];
    }

    /// <summary>Diagnostic dump of the header and every directory entry.</summary>
    public void Dump(TextWriter w)
    {
        w.WriteLine($"CFBF v{_majorVersion}, sector={_sectorSize}, mini={_miniSectorSize}, " +
                    $"fatSectors={_numFatSectors}, dirSectorsHeader={_numDirectorySectors}, " +
                    $"fatEntries={_fat.Length}, miniFatEntries={_miniFat.Length}, " +
                    $"miniStream={_miniStream.Length} bytes, file={_stream.Length} bytes");
        w.WriteLine($"Directory entries read: {_entries.Count}");
        w.WriteLine("  id type reach nameLen  left      right     child     start       size  path/name");
        foreach (var e in _entries)
        {
            if (e.Type == 0 && string.IsNullOrEmpty(e.Name)) continue; // unused slot
            string t = e.Type switch { 1 => "STG", 2 => "STM", 5 => "ROOT", 0 => "free", _ => $"?{e.Type}" };
            string shown = e.Reachable ? (e.Path.Length == 0 ? "(root)" : e.Path) : "[ORPHAN] " + e.Name;
            w.WriteLine($"{e.Id,4} {t,-4} {(e.Reachable ? "yes" : "NO"),-5} {e.RawNameLength,7} " +
                        $"{Fmt(e.LeftSibling)} {Fmt(e.RightSibling)} {Fmt(e.Child)} " +
                        $"{Fmt(e.StartSector)} {e.Size,10}  {shown}");
        }

        static string Fmt(uint v) => v == CfbConstants.NoStream ? "    -    " : $"{v,9}";
    }

    private void ParseDirectory(byte[] bytes)
    {
        if (bytes.Length % 128 != 0)
            throw new InvalidDataException("Directory stream is not aligned to 128-byte entries.");

        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms, Encoding.Unicode);

        int id = 0;
        while (ms.Position + 128 <= ms.Length)
        {
            byte[] nameBytes = br.ReadBytes(64);
            ushort nameLength = br.ReadUInt16();
            byte type = br.ReadByte();
            br.ReadByte(); // color

            uint left = br.ReadUInt32();
            uint right = br.ReadUInt32();
            uint child = br.ReadUInt32();

            br.ReadBytes(16); // CLSID
            br.ReadUInt32();  // state bits
            br.ReadUInt64();  // creation time
            br.ReadUInt64();  // modified time

            uint start = br.ReadUInt32();
            ulong size = br.ReadUInt64();
            // [MS-CFB] 2.6: a directory entry is exactly 128 bytes and ends
            // with the 8-byte stream size. There is no trailing reserved field;
            // reading one shifts every following entry by 4 bytes.

            // Decode up to the UTF-16 null terminator instead of trusting the
            // length field: some writers store a wrong length (a char count,
            // or no terminator), which would truncate every name.
            int charCount = 0;
            while (charCount < 32 &&
                   (nameBytes[charCount * 2] | nameBytes[charCount * 2 + 1]) != 0)
                charCount++;
            string name = charCount == 0
                ? ""
                : Encoding.Unicode.GetString(nameBytes, 0, charCount * 2);

            // [MS-CFB] 2.6.3: in version 3 files the high DWORD of the stream
            // size may contain garbage and must be ignored.
            if (_majorVersion == 3)
                size &= 0xFFFFFFFFUL;

            _entries.Add(new DirectoryEntry
            {
                Id = id++,
                Name = name,
                Type = type,
                LeftSibling = left,
                RightSibling = right,
                Child = child,
                StartSector = start,
                Size = size,
                RawNameLength = nameLength
            });
        }

        _rootEntry = _entries.FirstOrDefault(e => e.Type == 5);
        if (_rootEntry == null)
            throw new InvalidDataException("CFBF root directory entry is missing.");
    }

    private void BuildPaths()
    {
        if (_rootEntry == null)
            throw new InvalidDataException("CFBF root directory entry is missing.");

        _byPath.Clear();
        _rootEntry.Path = "";
        _rootEntry.Reachable = true;

        // CFBF directory children are red/black sibling trees.  A number of
        // real-world MSG producers are slightly lax about directory links,
        // so do not reject a file merely because an entry is referenced more
        // than once.  What must be prevented is recursion through an entry
        // already on the current call stack.
        var active = new HashSet<int>();
        WalkSiblingTree(_rootEntry.Child, "", active);
    }

    private void WalkSiblingTree(uint id, string parentPath, HashSet<int> active)
    {
        if (id == CfbConstants.NoStream)
            return;

        if (id >= _entries.Count)
            throw new InvalidDataException(
                $"Directory entry index out of range: {id}.");

        int index = checked((int)id);

        // A repeated entry on the current recursion path is a genuine cycle.
        // Stop this branch instead of looping forever; this also lets us read
        // otherwise usable MSG files with damaged directory metadata.
        if (!active.Add(index))
            return;

        try
        {
            var e = _entries[index];

            // Left/right links are siblings and therefore use the same parent path.
            WalkSiblingTree(e.LeftSibling, parentPath, active);

            string currentPath = string.IsNullOrEmpty(parentPath)
                ? e.Name
                : parentPath + "\\" + e.Name;

            // If a malformed file references the same directory entry from a
            // second branch, retain the first path rather than replacing it.
            if (!e.Reachable)
            {
                e.Reachable = true;
                e.Path = currentPath;
                string normalized = NormalizePath(currentPath);
                if (!_byPath.ContainsKey(normalized))
                    _byPath[normalized] = e;
            }

            if (e.Type == 1) // storage
                WalkSiblingTree(e.Child, currentPath, active);

            WalkSiblingTree(e.RightSibling, parentPath, active);
        }
        finally
        {
            active.Remove(index);
        }
    }

    private byte[] ReadPhysicalSector(uint sector)
    {
        long offset = checked(((long)sector + 1L) * _sectorSize);
        if (offset < 0 || offset > _stream.Length - _sectorSize)
            throw new InvalidDataException($"Physical sector {sector} is outside the file.");

        _stream.Position = offset;
        return ReadExactly(_sectorSize);
    }

    private byte[] ReadExactly(int count)
    {
        byte[] result = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = _stream.Read(result, total, count - total);
            if (n == 0)
                throw new EndOfStreamException();
            total += n;
        }
        return result;
    }

    private static uint[] BytesToUInt32(byte[] bytes)
    {
        int count = bytes.Length / 4;
        var result = new uint[count];
        Buffer.BlockCopy(bytes, 0, result, 0, count * 4);
        return result;
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').TrimStart('\\');

    public void Dispose()
    {
        _br.Dispose();
        _stream.Dispose();
    }
}
