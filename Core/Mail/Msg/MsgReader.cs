using System.Text;

namespace CsAgent.Core.Mail.Msg;

// Reader for ordinary Outlook .msg files ([MS-OXMSG]) on top of CompoundFile.
// Reads headers, recipients, bodies (text / HTML / compressed RTF) and normal attachments.

public static class MsgReader
{
    static MsgReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const ushort PT_I2 = 0x0002;
    private const ushort PT_LONG = 0x0003;
    private const ushort PT_R4 = 0x0004;
    private const ushort PT_DOUBLE = 0x0005;
    private const ushort PT_CURRENCY = 0x0006;
    private const ushort PT_APPTIME = 0x0007;
    private const ushort PT_ERROR = 0x000A;
    private const ushort PT_BOOLEAN = 0x000B;
    private const ushort PT_OBJECT = 0x000D;
    private const ushort PT_I8 = 0x0014;
    private const ushort PT_STRING8 = 0x001E;
    private const ushort PT_UNICODE = 0x001F;
    private const ushort PT_SYSTIME = 0x0040;
    private const ushort PT_CLSID = 0x0048;
    private const ushort PT_BINARY = 0x0102;
    private const ushort MV_FLAG = 0x1000;

    private static readonly Guid PsMapi =
        new("00020328-0000-0000-C000-000000000046");

    private static readonly Guid PsPublicStrings =
        new("00020329-0000-0000-C000-000000000046");

    private static readonly Dictionary<ushort, string> CommonNames = new()
    {
        [0x001A] = "MessageClass",
        [0x0037] = "Subject",
        [0x003D] = "SubjectPrefix",
        [0x0042] = "SentRepresentingName",
        [0x005A] = "SentRepresentingEmail",
        [0x0070] = "ConversationTopic",
        [0x0071] = "ConversationIndex",
        [0x0C15] = "RecipientType",
        [0x0C1A] = "SenderName",
        [0x0C1F] = "SenderEmail",
        [0x0E02] = "DisplayBcc",
        [0x0E03] = "DisplayCc",
        [0x0E04] = "DisplayTo",
        [0x0E06] = "ReceivedTime",
        [0x0E07] = "MessageFlags",
        [0x0E08] = "MessageSize",
        [0x0E1D] = "NormalizedSubject",
        [0x1000] = "Body",
        [0x1009] = "RtfCompressed",
        [0x1013] = "Html",
        [0x1035] = "InternetMessageId",
        [0x3001] = "DisplayName",
        [0x3002] = "AddressType",
        [0x3003] = "EmailAddress",
        [0x39FE] = "SmtpAddress",
        [0x3701] = "AttachData",
        [0x3703] = "AttachExtension",
        [0x3704] = "AttachFilename",
        [0x3705] = "AttachMethod",
        [0x3707] = "AttachLongFilename",
        [0x370E] = "AttachMimeTag",
        [0x370F] = "AttachRendering",
        [0x3710] = "AttachFlags",
        [0x3711] = "AttachTransportName",
        [0x3712] = "AttachLongPathname",
        [0x3713] = "AttachMimeSequence",
        [0x0FF9] = "RecordKey",
        [0x0FFF] = "EntryId",
    };

    public static MsgMessage Read(string msgPath, MsgReaderOptions? options = null)
    {
        if (msgPath == null) throw new ArgumentNullException(nameof(msgPath));
        if (!File.Exists(msgPath))
            throw new FileNotFoundException("MSG file not found.", msgPath);

        options ??= new MsgReaderOptions();

        using var cf = new CompoundFile(msgPath, options.MaxStreamBytes);
        var context = new ReadContext(cf, options);
        return ReadMessage(context, "", 0);
    }

    private sealed class ReadContext
    {
        public CompoundFile File { get; }
        public MsgReaderOptions Options { get; }
        public HashSet<string> ActiveMessages { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ReadContext(CompoundFile file, MsgReaderOptions options)
        {
            File = file;
            Options = options;
        }
    }

    private static MsgMessage ReadMessage(
        ReadContext context,
        string storagePath,
        int depth)
    {
        if (depth > context.Options.MaxEmbeddedMessageDepth)
            throw new InvalidDataException("Maximum embedded-message depth exceeded.");

        string key = storagePath + "|message";
        if (!context.ActiveMessages.Add(key))
            throw new InvalidDataException("Recursive embedded message detected.");

        try
        {
            var msg = new MsgMessage();
            // [MS-OXMSG] 2.4.1: top-level header = 32 bytes, embedded = 24.
            var properties = ReadProperties(context, storagePath,
                depth == 0 ? 32 : 24);
            msg.SetProperties(properties);

            msg.Subject = GetString(context, storagePath, properties, 0x0037);
            msg.SenderName = GetString(context, storagePath, properties, 0x0C1A);
            msg.SenderEmail = GetString(context, storagePath, properties, 0x0C1F);
            msg.SenderAddressType = GetString(context, storagePath, properties, 0x0C1E);
            msg.SmtpSender = GetString(context, storagePath, properties, 0x5D01);
            msg.MessageClass = GetString(context, storagePath, properties, 0x001A);
            msg.InternetMessageId = GetString(context, storagePath, properties, 0x1035);
            msg.DisplayTo = GetString(context, storagePath, properties, 0x0E04);
            msg.DisplayCc = GetString(context, storagePath, properties, 0x0E03);
            msg.DisplayBcc = GetString(context, storagePath, properties, 0x0E02);
            msg.ConversationTopic = GetString(context, storagePath, properties, 0x0070);
            msg.NormalizedSubject = GetString(context, storagePath, properties, 0x0E1D);

            msg.BodyPlain = GetString(context, storagePath, properties, 0x1000);

            byte[]? html = GetBytes(context, storagePath, properties, 0x1013);
            if (html != null)
                msg.BodyHtml = DecodeText(html, GetInt32(properties, 0x3FDE) ?? 65001);

            msg.BodyRtfCompressed = GetBytes(context, storagePath, properties, 0x1009);
            if (msg.BodyRtfCompressed != null)
            {
                try
                {
                    msg.BodyRtf = RtfCompression.Decompress(msg.BodyRtfCompressed);
                }
                catch when (!context.Options.Strict)
                {
                    msg.BodyRtf = null;
                }
            }

            if (TryGetDateTime(properties, 0x0E06, out var received))
                msg.ReceivedTime = received;
            if (TryGetDateTime(properties, 0x0039, out var sent))
                msg.SentTime = sent;

            msg.Importance = GetInt32(properties, 0x0017);
            msg.Sensitivity = GetInt32(properties, 0x0036);
            msg.MessageFlags = GetInt32(properties, 0x0E07);

            ReadRecipients(context, storagePath, msg);
            ReadAttachments(context, storagePath, msg, depth);

            return msg;
        }
        finally
        {
            context.ActiveMessages.Remove(key);
        }
    }

    private static Dictionary<uint, MsgProperty> ReadProperties(
        ReadContext context,
        string storagePath,
        int headerSize)
    {
        string path = Child(storagePath, "__properties_version1.0");
        byte[] bytes;
        try
        {
            bytes = context.File.GetStream(path);
        }
        catch (FileNotFoundException) when (!context.Options.Strict)
        {
            // The property stream is missing (damaged file or a non-Outlook
            // writer). Fixed-size values (dates, flags, integers) live only in
            // that stream and are lost, but strings and binaries have their own
            // __substg1.0_TTTTTTTT streams whose names encode the property tag.
            return RecoverPropertiesFromStreamNames(context, storagePath);
        }

        if (bytes.Length < headerSize)
            throw new InvalidDataException($"Invalid property stream: {path}");

        var resolver = NamedPropertyResolver.TryCreate(context.File);
        var result = new Dictionary<uint, MsgProperty>();

        for (int offset = headerSize; offset + 16 <= bytes.Length; offset += 16)
        {
            uint tag = BitConverter.ToUInt32(bytes, offset);
            uint flags = BitConverter.ToUInt32(bytes, offset + 4);
            uint size = BitConverter.ToUInt32(bytes, offset + 8);
            uint reserved = BitConverter.ToUInt32(bytes, offset + 12);
            ushort id = (ushort)(tag >> 16);
            ushort type = (ushort)tag;

            string name = CommonNames.TryGetValue(id, out var common)
                ? common
                : $"Prop_{id:X4}";

            string? namedName = null;
            if (id >= 0x8000 && resolver != null)
            {
                namedName = resolver.Resolve(id);
                if (namedName != null)
                    name = namedName;
            }

            var property = new MsgProperty
            {
                Tag = tag,
                Id = id,
                Type = type,
                Flags = flags,
                Size = size,
                Reserved = reserved,
                Name = name,
                NamedProperty = namedName
            };

            if (!IsVariableOrObject(type) && (type & MV_FLAG) == 0)
            {
                property.Value = DecodeFixed(type, bytes, offset + 8);
            }

            result[tag] = property;
        }

        // Resolve values only after the complete property table is known.
        foreach (var property in result.Values)
        {
            if (property.Value != null)
                continue;

            // Attachment bytes are the big ones: skip them when only a listing is wanted.
            if (!context.Options.ReadAttachmentData && property.Id == 0x3701 &&
                (property.Type & 0x0FFF) == PT_BINARY)
                continue;

            try
            {
                property.Value = ReadPropertyValue(
                    context, storagePath, property, result);
            }
            catch when (!context.Options.Strict)
            {
                property.Value = null;
            }
        }

        return result;
    }

    private static Dictionary<uint, MsgProperty> RecoverPropertiesFromStreamNames(
        ReadContext context,
        string storagePath)
    {
        const string prefix = "__substg1.0_";
        var resolver = NamedPropertyResolver.TryCreate(context.File);
        var result = new Dictionary<uint, MsgProperty>();

        var names = context.File.GetChildStreamNames(storagePath, includeOrphans: false);
        // If the root's tree is broken, fall back to streams that were not
        // reachable from any storage (they most likely belonged to the root).
        if (storagePath.Length == 0 && !names.Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            names = context.File.GetChildStreamNames(storagePath, includeOrphans: true);

        foreach (string name in names)
        {
            // Only plain single-value streams: "__substg1.0_" + 8 hex digits.
            if (name.Length != prefix.Length + 8 ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out uint tag))
                continue;

            ushort id = (ushort)(tag >> 16);
            ushort type = (ushort)tag;
            string propName = CommonNames.TryGetValue(id, out var common) ? common : $"Prop_{id:X4}";
            string? namedName = id >= 0x8000 ? resolver?.Resolve(id) : null;

            var property = new MsgProperty
            {
                Tag = tag,
                Id = id,
                Type = type,
                Name = namedName ?? propName,
                NamedProperty = namedName
            };

            try
            {
                property.Value = ReadPropertyValue(context, storagePath, property, result);
            }
            catch when (!context.Options.Strict)
            {
                property.Value = null;
            }

            result[tag] = property;
        }

        return result;
    }

    private static object? ReadPropertyValue(
        ReadContext context,
        string storagePath,
        MsgProperty property,
        Dictionary<uint, MsgProperty> all)
    {
        ushort type = property.Type;
        ushort baseType = (ushort)(type & 0x0FFF);
        bool multi = (type & MV_FLAG) != 0;

        if (baseType == PT_OBJECT)
            return null;

        if (!multi && IsVariableOrObject(type))
        {
            string streamName = StreamName(property.Tag);
            try
            {
                byte[] data = context.File.GetStream(Child(storagePath, streamName));
                EnforceSize(context, data.LongLength);
                return baseType == PT_UNICODE || baseType == PT_STRING8
                    ? DecodeText(data, baseType == PT_UNICODE ? 1200 : 1252)
                    : data;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        if (!multi)
            return null;

        if (IsFixedMulti(baseType))
        {
            byte[] data = context.File.GetStream(
                Child(storagePath, StreamName(property.Tag)));
            return DecodeFixedMulti(baseType, data);
        }

        // Variable multiple-valued properties have a length stream and
        // one value stream per element.
        byte[] lengths = context.File.GetStream(
            Child(storagePath, StreamName(property.Tag)));
        int width = LengthEntryWidth(baseType);

        if (width <= 0 || lengths.Length % width != 0)
            return null;

        int count = lengths.Length / width;
        var values = new object?[count];

        for (int i = 0; i < count; i++)
        {
            long len = ReadLength(lengths, i * width, width);
            if (len < 0 || len > context.Options.MaxStreamBytes)
                throw new InvalidDataException("Invalid multi-valued property length.");

            string valueName =
                $"{StreamName(property.Tag)}-{i:X8}";

            byte[] data = context.File.GetStream(
                Child(storagePath, valueName));

            if (data.LongLength != len && context.Options.Strict)
                throw new InvalidDataException(
                    $"Multi-valued property length mismatch for {property.Tag:X8}.");

            if (baseType == PT_UNICODE || baseType == PT_STRING8)
                values[i] = DecodeText(data, baseType == PT_UNICODE ? 1200 : 1252);
            else
                values[i] = data;
        }

        return values;
    }

    private static void ReadRecipients(
        ReadContext context,
        string storagePath,
        MsgMessage message)
    {
        var recipients = context.File.Entries
            .Where(e => e.Type == 1 &&
                        ParentOf(e.Path) == storagePath &&
                        e.Name.StartsWith("__recip_version1.0_#",
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count > context.Options.MaxRecipientsPerMessage)
            throw new InvalidDataException("Recipient count exceeds configured limit.");

        foreach (var entry in recipients)
        {
            // [MS-OXMSG] 2.4.2: recipient storages have an 8-byte header.
            var props = ReadProperties(context, entry.Path, 8);

            int rawType = GetInt32(props, 0x0C15) ?? 0;
            var recipient = new MsgRecipient
            {
                RawRecipientType = rawType,
                Type = rawType switch
                {
                    1 => RecipientType.To,
                    2 => RecipientType.Cc,
                    3 => RecipientType.Bcc,
                    _ => RecipientType.Unknown
                },
                DisplayName = GetString(context, entry.Path, props, 0x3001),
                AddressType = GetString(context, entry.Path, props, 0x3002),
                EmailAddress = GetString(context, entry.Path, props, 0x3003),
                SmtpAddress = GetString(context, entry.Path, props, 0x39FE),
                EntryId = GetBytes(context, entry.Path, props, 0x0FFF)
            };

            message.Recipients.Add(recipient);
        }
    }

    private static void ReadAttachments(
        ReadContext context,
        string storagePath,
        MsgMessage message,
        int depth)
    {
        var attachments = context.File.Entries
            .Where(e => e.Type == 1 &&
                        ParentOf(e.Path) == storagePath &&
                        e.Name.StartsWith("__attach_version1.0_#",
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (attachments.Count > context.Options.MaxAttachmentsPerMessage)
            throw new InvalidDataException("Attachment count exceeds configured limit.");

        foreach (var entry in attachments)
        {
            // [MS-OXMSG] 2.4.2: attachment storages have an 8-byte header.
            var props = ReadProperties(context, entry.Path, 8);
            int method = GetInt32(props, 0x3705) ?? 0;

            var attachment = new MsgAttachment
            {
                Method = method,
                FileName =
                    GetString(context, entry.Path, props, 0x3707) ??
                    GetString(context, entry.Path, props, 0x3704),
                LongFileName = GetString(context, entry.Path, props, 0x3707),
                DisplayName = GetString(context, entry.Path, props, 0x3001),
                Extension = GetString(context, entry.Path, props, 0x3703),
                PathName = GetString(context, entry.Path, props, 0x370D) ??
                           GetString(context, entry.Path, props, 0x3712),
                MimeType = GetString(context, entry.Path, props, 0x370E),
                Size = GetInt32(props, 0x0E20),
                RenderingPosition = GetInt32(props, 0x370B) ?? -1
            };

            if (method == 1) // afByValue
            {
                if (context.Options.ReadAttachmentData)
                {
                    byte[]? data = GetBytes(
                        context, entry.Path, props, 0x3701);
                    attachment.Data = data ?? Array.Empty<byte>();
                    attachment.Size = attachment.Data.LongLength;
                }
                else
                {
                    attachment.Size = context.File.GetStreamSize(
                        Child(entry.Path, StreamName(((uint)0x3701 << 16) | PT_BINARY)));
                }
            }
            else if (method == 5) // afEmbeddedMessage
            {
                string embeddedPath =
                    Child(entry.Path, "__substg1.0_3701000D");

                bool exists = context.File.Entries.Any(
                    e => e.Type == 1 &&
                         string.Equals(e.Path, embeddedPath,
                             StringComparison.OrdinalIgnoreCase));

                if (exists)
                {
                    attachment.EmbeddedMessage =
                        ReadMessage(context, embeddedPath, depth + 1);
                }
            }
            else if (method == 6 && context.Options.ReadCustomAttachmentData)
            {
                // afStorage is application-defined. Expose the storage path
                // through PathName but do not flatten arbitrary child streams.
                attachment.Data = Array.Empty<byte>();
            }

            if (attachment.FileName == null)
                attachment.FileName = attachment.DisplayName;

            message.Attachments.Add(attachment);
        }
    }

    private static string? GetString(
        ReadContext context,
        string storagePath,
        Dictionary<uint, MsgProperty> props,
        ushort id)
    {
        MsgProperty? p = FindString(props, id);
        if (p == null)
            return null;

        if (p.Value is string s)
            return s;

        try
        {
            byte[] bytes = context.File.GetStream(
                Child(storagePath, StreamName(p.Tag)));
            return DecodeText(bytes,
                (p.Type & 0x0FFF) == PT_UNICODE ? 1200 : 1252)
                .TrimEnd('\0');
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static byte[]? GetBytes(
        ReadContext context,
        string storagePath,
        Dictionary<uint, MsgProperty> props,
        ushort id)
    {
        MsgProperty? p = FindProperty(props, id);
        if (p == null)
            return null;

        if (p.Value is byte[] bytes)
            return bytes;

        try
        {
            byte[] data = context.File.GetStream(
                Child(storagePath, StreamName(p.Tag)));
            EnforceSize(context, data.LongLength);
            return data;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static MsgProperty? FindString(
        Dictionary<uint, MsgProperty> props, ushort id)
    {
        return FindProperty(props, id, PT_UNICODE, PT_STRING8);
    }

    private static MsgProperty? FindProperty(
        Dictionary<uint, MsgProperty> props,
        ushort id,
        params ushort[] types)
    {
        if (types.Length == 0)
        {
            foreach (var p in props.Values)
                if (p.Id == id) return p;
            return null;
        }

        foreach (ushort type in types)
        {
            uint tag = ((uint)id << 16) | type;
            if (props.TryGetValue(tag, out var p))
                return p;
        }

        return null;
    }

    private static int? GetInt32(
        Dictionary<uint, MsgProperty> props, ushort id)
    {
        var p = FindProperty(props, id, PT_LONG);
        return p?.Value is int value ? value : null;
    }

    private static bool TryGetDateTime(
        Dictionary<uint, MsgProperty> props,
        ushort id,
        out DateTime value)
    {
        var p = FindProperty(props, id, PT_SYSTIME);
        if (p?.Value is DateTime dt)
        {
            value = dt;
            return true;
        }

        value = default;
        return false;
    }

    private static object? DecodeFixed(
        ushort type, byte[] bytes, int offset)
    {
        int width = FixedWidth(type);
        if (width <= 0 || offset < 0 || offset + width > bytes.Length)
            return null;

        switch (type)
        {
            case PT_I2: return BitConverter.ToInt16(bytes, offset);
            case PT_LONG: return BitConverter.ToInt32(bytes, offset);
            case PT_R4: return BitConverter.ToSingle(bytes, offset);
            case PT_DOUBLE:
            case PT_APPTIME: return BitConverter.ToDouble(bytes, offset);
            case PT_CURRENCY:
            case PT_I8: return BitConverter.ToInt64(bytes, offset);
            case PT_BOOLEAN:
                return BitConverter.ToUInt16(bytes, offset) != 0;
            case PT_ERROR:
                return BitConverter.ToUInt32(bytes, offset);
            case PT_SYSTIME:
                try
                {
                    return DateTime.FromFileTimeUtc(
                    BitConverter.ToInt64(bytes, offset));
                }
                catch { return null; }
            case PT_CLSID:
                return new Guid(new ReadOnlySpan<byte>(
                    bytes, offset, 16));
            default:
                return null;
        }
    }

    private static object DecodeFixedMulti(
        ushort baseType, byte[] bytes)
    {
        int width = FixedWidth(baseType);
        if (width <= 0 || bytes.Length % width != 0)
            return Array.Empty<object>();

        var result = new object[bytes.Length / width];
        for (int i = 0; i < result.Length; i++)
            result[i] = DecodeFixed(baseType, bytes, i * width)!;
        return result;
    }

    private static int FixedWidth(ushort type) => type switch
    {
        PT_I2 or PT_BOOLEAN => 2,
        PT_LONG or PT_R4 or PT_ERROR => 4,
        PT_DOUBLE or PT_APPTIME or PT_CURRENCY or PT_I8 or PT_SYSTIME => 8,
        PT_CLSID => 16,
        _ => 0
    };

    private static bool IsVariableOrObject(ushort type)
    {
        ushort baseType = (ushort)(type & 0x0FFF);
        return baseType is PT_STRING8 or PT_UNICODE or PT_BINARY
            or PT_CLSID or PT_OBJECT;
    }

    private static bool IsFixedMulti(ushort type) =>
        FixedWidth(type) > 0;

    private static int LengthEntryWidth(ushort type) =>
        type is PT_I2 or PT_BOOLEAN ? 2 : 4;

    private static long ReadLength(byte[] bytes, int offset, int width) =>
        width == 2
            ? BitConverter.ToUInt16(bytes, offset)
            : BitConverter.ToUInt32(bytes, offset);

    private static string StreamName(uint tag) =>
        $"__substg1.0_{tag:X8}";

    private static string Child(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;

    private static string ParentOf(string path)
    {
        int i = path.LastIndexOf('\\');
        return i < 0 ? "" : path[..i];
    }

    private static string DecodeText(byte[] bytes, int codePage)
    {
        if (bytes.Length == 0) return "";

        Encoding encoding;
        try
        {
            encoding = codePage == 1200
                ? Encoding.Unicode
                : Encoding.GetEncoding(
                    codePage > 0 ? codePage : 1252,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
        }
        catch
        {
            encoding = Encoding.GetEncoding(1252);
        }

        return encoding.GetString(bytes).TrimEnd('\0');
    }

    private static void EnforceSize(ReadContext context, long size)
    {
        if (size > context.Options.MaxStreamBytes)
            throw new InvalidDataException(
                $"Stream exceeds configured limit ({context.Options.MaxStreamBytes} bytes).");
    }

    private static class RtfCompression
    {
        private const uint CompressedMagic = 0x75465A4C;   // "LZFu"
        private const uint UncompressedMagic = 0x414C454D; // "MELA"

        // MS-OXRTFCP initial dictionary. It is deliberately kept as bytes,
        // because the compression algorithm works on an 8-bit dictionary.
        private static readonly byte[] Prebuffer = Encoding.ASCII.GetBytes(
            "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil " +
            "\\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans Serif" +
            "SymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0" +
            "\\blue0\\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

        public static string Decompress(byte[] packed)
        {
            if (packed.Length < 16)
                throw new InvalidDataException("RTF compressed property is too short.");

            uint compSize = BitConverter.ToUInt32(packed, 0);
            uint rawSize = BitConverter.ToUInt32(packed, 4);
            uint type = BitConverter.ToUInt32(packed, 8);
            uint expectedCrc = BitConverter.ToUInt32(packed, 12);

            int contentLength = checked((int)Math.Min(
                (uint)Math.Max(0, packed.Length - 16),
                compSize >= 12 ? compSize - 12 : 0));

            if (contentLength < 0 || 16 + contentLength > packed.Length)
                throw new InvalidDataException("Invalid RTF compressed size.");

            byte[] content = new byte[contentLength];
            Buffer.BlockCopy(packed, 16, content, 0, contentLength);

            if (rawSize > int.MaxValue)
                throw new InvalidDataException("RTF uncompressed size is too large.");

            if (type == UncompressedMagic)
            {
                if (rawSize > content.Length)
                    throw new InvalidDataException("Truncated uncompressed RTF.");
                return Encoding.ASCII.GetString(
                    content, 0, checked((int)rawSize));
            }

            if (type != CompressedMagic)
                throw new InvalidDataException("Unknown RTF compression type.");

            if (Crc32(content) != expectedCrc)
                throw new InvalidDataException("RTF compressed CRC mismatch.");

            byte[] dictionary = new byte[4096];
            Buffer.BlockCopy(
                Prebuffer, 0, dictionary, 0,
                Math.Min(Prebuffer.Length, dictionary.Length));

            int write = Prebuffer.Length;
            using var output = new MemoryStream(
                checked((int)Math.Min(rawSize, int.MaxValue)));

            int input = 0;
            while (input < content.Length && output.Length < rawSize)
            {
                byte control = content[input++];

                for (int bit = 0; bit < 8 && input < content.Length &&
                     output.Length < rawSize; bit++)
                {
                    bool reference = (control & (1 << bit)) != 0;

                    if (!reference)
                    {
                        byte literal = content[input++];
                        dictionary[write & 0xFFF] = literal;
                        write = (write + 1) & 0xFFF;
                        output.WriteByte(literal);
                        continue;
                    }

                    if (input + 2 > content.Length)
                        throw new InvalidDataException("Truncated RTF reference.");

                    // References are stored in big-endian byte order.
                    ushort token = (ushort)((content[input] << 8) |
                                             content[input + 1]);
                    input += 2;

                    int offset = (token >> 4) & 0x0FFF;
                    int length = (token & 0x0F) + 2;

                    for (int j = 0; j < length &&
                         output.Length < rawSize; j++)
                    {
                        byte b = dictionary[offset];
                        offset = (offset + 1) & 0x0FFF;

                        dictionary[write] = b;
                        write = (write + 1) & 0x0FFF;
                        output.WriteByte(b);
                    }
                }
            }

            if (output.Length != rawSize)
                throw new InvalidDataException(
                    $"RTF decompression produced {output.Length} bytes; expected {rawSize}.");

            return Encoding.ASCII.GetString(output.ToArray());
        }

        private static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0
                        ? (crc >> 1) ^ 0xEDB88320u
                        : crc >> 1;
            }
            return ~crc;
        }
    }

    private sealed class NamedPropertyResolver
    {
        private readonly Guid[] _guids;
        private readonly byte[] _entries;
        private readonly byte[] _strings;

        private NamedPropertyResolver(
            Guid[] guids, byte[] entries, byte[] strings)
        {
            _guids = guids;
            _entries = entries;
            _strings = strings;
        }

        public static NamedPropertyResolver? TryCreate(CompoundFile file)
        {
            try
            {
                byte[] guidBytes = file.GetStream(
                    "__nameid_version1.0\\__substg1.0_00020102");
                byte[] entries = file.GetStream(
                    "__nameid_version1.0\\__substg1.0_00030102");
                byte[] strings = file.GetStream(
                    "__nameid_version1.0\\__substg1.0_00040102");

                if (guidBytes.Length % 16 != 0 ||
                    entries.Length % 8 != 0)
                    return null;

                var guids = new Guid[guidBytes.Length / 16];
                for (int i = 0; i < guids.Length; i++)
                    guids[i] = new Guid(new ReadOnlySpan<byte>(
                        guidBytes, i * 16, 16));

                return new NamedPropertyResolver(
                    guids, entries, strings);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public string? Resolve(ushort propertyId)
        {
            int index = propertyId - 0x8000;
            int offset = checked(index * 8);

            if (offset < 0 || offset + 8 > _entries.Length)
                return null;

            uint nameIdentifier = BitConverter.ToUInt32(
                _entries, offset);
            uint kindInfo = BitConverter.ToUInt32(
                _entries, offset + 4);

            ushort propertyIndex = (ushort)(kindInfo & 0xFFFF);
            int guidIndex = (int)((kindInfo >> 16) & 0x7FFF);
            bool isString = (kindInfo & 0x80000000) != 0;

            if (propertyIndex != index)
                return null;

            Guid guid = guidIndex switch
            {
                1 => PsMapi,
                2 => PsPublicStrings,
                _ => guidIndex >= 3 &&
                     guidIndex - 3 < _guids.Length
                    ? _guids[guidIndex - 3]
                    : Guid.Empty
            };

            if (guid == Guid.Empty)
                return null;

            if (!isString)
                return $"{{{guid}}}:LID:0x{nameIdentifier:X8}";

            if (nameIdentifier >= _strings.Length)
                return null;

            int p = checked((int)nameIdentifier);
            if (p + 4 > _strings.Length)
                return null;

            int byteLength = BitConverter.ToInt32(_strings, p);
            if (byteLength < 0 || p + 4 + byteLength > _strings.Length ||
                (byteLength & 1) != 0)
                return null;

            string name = Encoding.Unicode.GetString(
                _strings, p + 4, byteLength);

            return $"{{{guid}}}:{name}";
        }
    }
}
