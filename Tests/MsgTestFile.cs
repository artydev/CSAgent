using System.Text;

namespace CsAgent.Tests;

/// <summary>
/// Builds small, valid Outlook .msg files (Compound File v3 + MAPI streams) so the reader can be
/// tested without shipping binary samples. Only the parts the reader needs are written.
/// </summary>
sealed class MsgTestFile
{
    // ── MAPI property helpers ─────────────────────────────────────────────
    public sealed class Storage
    {
        public string Name = "";
        public int HeaderSize;
        readonly List<(uint tag, byte[] fixedBytes, byte[]? stream)> _props = new();
        public List<Storage> Children { get; } = new();
        public List<(string name, byte[] data)> RawStreams { get; } = new();

        public Storage Str(ushort id, string value)
        {
            var data = Encoding.Unicode.GetBytes(value + "\0");
            _props.Add((((uint)id << 16) | 0x001F, new byte[0], data));
            return this;
        }
        public Storage Bin(ushort id, byte[] value)
        {
            _props.Add((((uint)id << 16) | 0x0102, new byte[0], value));
            return this;
        }
        public Storage Long(ushort id, int value)
        {
            _props.Add((((uint)id << 16) | 0x0003, BitConverter.GetBytes(value), null));
            return this;
        }
        public Storage Time(ushort id, DateTime utc)
        {
            _props.Add((((uint)id << 16) | 0x0040, BitConverter.GetBytes(utc.ToFileTimeUtc()), null));
            return this;
        }

        internal byte[] PropertyStream(int recipients, int attachments)
        {
            using var ms = new MemoryStream();
            var header = new byte[HeaderSize];
            if (HeaderSize == 32)
            {
                BitConverter.GetBytes(recipients).CopyTo(header, 16);
                BitConverter.GetBytes(attachments).CopyTo(header, 20);
                BitConverter.GetBytes(recipients).CopyTo(header, 8);
                BitConverter.GetBytes(attachments).CopyTo(header, 12);
            }
            ms.Write(header);
            foreach (var (tag, fixedBytes, stream) in _props)
            {
                ms.Write(BitConverter.GetBytes(tag));
                ms.Write(BitConverter.GetBytes(6u));                       // flags: readable | writable
                if (stream != null)
                {
                    ms.Write(BitConverter.GetBytes((uint)stream.Length));
                    ms.Write(new byte[4]);
                }
                else
                {
                    var v = new byte[8];
                    fixedBytes.CopyTo(v, 0);
                    ms.Write(v);
                }
            }
            return ms.ToArray();
        }

        internal IEnumerable<(string name, byte[] data)> Streams(int recipients, int attachments)
        {
            yield return ("__properties_version1.0", PropertyStream(recipients, attachments));
            foreach (var (tag, _, stream) in _props)
                if (stream != null) yield return ($"__substg1.0_{tag:X8}", stream);
            foreach (var r in RawStreams) yield return r;
        }
    }

    public Storage Root { get; } = new() { Name = "Root Entry", HeaderSize = 32 };

    public MsgTestFile()
    {
        Root.Str(0x001A, "IPM.Note");
    }

    public Storage AddRecipient(string name, string smtp, int type)
    {
        int n = Root.Children.Count(c => c.Name.StartsWith("__recip_version1.0_#"));
        var s = new Storage { Name = $"__recip_version1.0_#{n:X8}", HeaderSize = 8 };
        s.Long(0x0C15, type).Str(0x3001, name).Str(0x3002, "SMTP").Str(0x3003, smtp).Str(0x39FE, smtp);
        Root.Children.Add(s);
        return s;
    }

    public Storage AddAttachment(string fileName, byte[] data, string? mime = null)
    {
        int n = Root.Children.Count(c => c.Name.StartsWith("__attach_version1.0_#"));
        var s = new Storage { Name = $"__attach_version1.0_#{n:X8}", HeaderSize = 8 };
        s.Long(0x3705, 1).Str(0x3707, fileName).Str(0x3704, fileName).Bin(0x3701, data);
        if (mime != null) s.Str(0x370E, mime);
        Root.Children.Add(s);
        return s;
    }

    public Storage AddEmbeddedMessage(string subject, string body)
    {
        int n = Root.Children.Count(c => c.Name.StartsWith("__attach_version1.0_#"));
        var s = new Storage { Name = $"__attach_version1.0_#{n:X8}", HeaderSize = 8 };
        s.Long(0x3705, 5).Str(0x3707, subject + ".msg");
        var inner = new Storage { Name = "__substg1.0_3701000D", HeaderSize = 24 };
        inner.Str(0x0037, subject).Str(0x1000, body).Str(0x001A, "IPM.Note");
        s.Children.Add(inner);
        Root.Children.Add(s);
        return s;
    }

    // ── Compound File writer ──────────────────────────────────────────────
    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, NoStream = 0xFFFFFFFF;
    sealed class Entry { public string Name = ""; public byte Type; public uint Left = NoStream, Right = NoStream, Child = NoStream, Start = EndOfChain; public ulong Size; public byte[]? Data; }

    public byte[] Build()
    {
        // Flatten the tree into directory entries.
        var entries = new List<Entry> { new() { Name = "Root Entry", Type = 5 } };
        void Add(Storage s, int parentIndex)
        {
            int recips = s.Children.Count(c => c.Name.StartsWith("__recip_version1.0_#"));
            int attach = s.Children.Count(c => c.Name.StartsWith("__attach_version1.0_#"));
            var kids = new List<int>();
            foreach (var (name, data) in s.Streams(recips, attach))
            {
                entries.Add(new Entry { Name = name, Type = 2, Data = data, Size = (ulong)data.Length });
                kids.Add(entries.Count - 1);
            }
            foreach (var c in s.Children)
            {
                entries.Add(new Entry { Name = c.Name, Type = 1 });
                int ci = entries.Count - 1;
                kids.Add(ci);
                Add(c, ci);
            }
            // The reader walks sibling links in any shape: chain the children through Right.
            if (kids.Count > 0)
            {
                entries[parentIndex].Child = (uint)kids[0];
                for (int k = 0; k + 1 < kids.Count; k++) entries[kids[k]].Right = (uint)kids[k + 1];
            }
        }
        Add(Root, 0);

        // Mini stream (streams < 4096 bytes) and big streams.
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        foreach (var e in entries.Where(x => x.Type == 2 && x.Size > 0 && x.Size < 4096))
        {
            e.Start = (uint)miniFat.Count;
            int n = (int)((e.Size + 63) / 64);
            for (int k = 0; k < n; k++) miniFat.Add(k == n - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
            mini.Write(e.Data!);
            mini.Write(new byte[n * 64 - e.Data!.Length]);
        }
        var miniBytes = mini.ToArray();

        int dirSectors = (entries.Count + 3) / 4;
        int miniFatSectors = (miniFat.Count + 127) / 128;
        int miniStreamSectors = (miniBytes.Length + 511) / 512;
        var big = entries.Where(x => x.Type == 2 && x.Size >= 4096).ToList();
        int bigSectors = big.Sum(e => (int)((e.Size + 511) / 512));
        int content = dirSectors + miniFatSectors + miniStreamSectors + bigSectors;
        int fatSectors = 1;
        while (fatSectors * 128 < content + fatSectors) fatSectors++;
        if (fatSectors > 109) throw new InvalidOperationException("test file too large for the builder");

        var fat = new uint[fatSectors * 128];
        Array.Fill(fat, FreeSect);
        int next = 0;
        for (int i = 0; i < fatSectors; i++) fat[next++] = FatSect;
        uint Chain(int count)
        {
            if (count == 0) return EndOfChain;
            uint first = (uint)next;
            for (int k = 0; k < count; k++) { fat[next] = k == count - 1 ? EndOfChain : (uint)(next + 1); next++; }
            return first;
        }
        uint firstDir = Chain(dirSectors);
        uint firstMiniFat = Chain(miniFatSectors);
        uint miniStart = Chain(miniStreamSectors);
        foreach (var e in big) e.Start = Chain((int)((e.Size + 511) / 512));
        entries[0].Start = miniStart;
        entries[0].Size = (ulong)miniBytes.Length;

        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        // Header (512 bytes)
        w.Write(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        w.Write(new byte[16]);
        w.Write((ushort)0x3E); w.Write((ushort)3); w.Write((ushort)0xFFFE);
        w.Write((ushort)9); w.Write((ushort)6); w.Write(new byte[6]);
        w.Write(0u);                       // directory sector count (0 for v3)
        w.Write((uint)fatSectors);
        w.Write(firstDir);
        w.Write(0u);
        w.Write(4096u);
        w.Write(miniFatSectors > 0 ? firstMiniFat : EndOfChain);
        w.Write((uint)miniFatSectors);
        w.Write(EndOfChain); w.Write(0u);  // no extra DIFAT
        for (int i = 0; i < 109; i++) w.Write(i < fatSectors ? (uint)i : FreeSect);

        void Pad() { while (file.Length % 512 != 0) file.WriteByte(0); }
        foreach (var f in fat) w.Write(f);
        foreach (var e in entries) WriteEntry(w, e);
        for (long pad = dirSectors * 512L - entries.Count * 128L; pad > 0; pad -= 128) w.Write(new byte[128]);   // unused slots
        foreach (var m in miniFat) w.Write(m);
        for (int pad = miniFat.Count; pad < miniFatSectors * 128; pad++) w.Write(FreeSect);
        w.Write(miniBytes); Pad();
        foreach (var e in big) { w.Write(e.Data!); Pad(); }
        return file.ToArray();
    }

    static void WriteEntry(BinaryWriter w, Entry e)
    {
        var name = new byte[64];
        var nb = Encoding.Unicode.GetBytes(e.Name + "\0");
        if (nb.Length > 64) throw new InvalidOperationException("name too long: " + e.Name);
        nb.CopyTo(name, 0);
        w.Write(name);
        w.Write((ushort)nb.Length);
        w.Write(e.Type);
        w.Write((byte)1);                  // black
        w.Write(e.Left); w.Write(e.Right); w.Write(e.Child);
        w.Write(new byte[16]); w.Write(0u); w.Write(0UL); w.Write(0UL);
        w.Write(e.Start); w.Write(e.Size);
    }
}
