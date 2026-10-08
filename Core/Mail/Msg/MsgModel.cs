namespace CsAgent.Core.Mail.Msg;

public enum RecipientType
{
    Unknown = 0,
    To = 1,
    Cc = 2,
    Bcc = 3
}

public sealed class MsgRecipient
{
    public string? DisplayName { get; set; }
    public string? AddressType { get; set; }
    public string? EmailAddress { get; set; }
    public string? SmtpAddress { get; set; }
    public RecipientType Type { get; set; }
    public int RawRecipientType { get; internal set; }
    public byte[]? EntryId { get; internal set; }
}

public sealed class MsgProperty
{
    public uint Tag { get; internal set; }
    public ushort Id { get; internal set; }
    public ushort Type { get; internal set; }
    public uint Flags { get; internal set; }
    public uint Size { get; internal set; }
    public uint Reserved { get; internal set; }
    public string Name { get; internal set; } = "";
    public string? NamedProperty { get; internal set; }
    public object? Value { get; internal set; }
}

public sealed class MsgMessage
{
    public string? Subject { get; set; }
    public string? SenderName { get; set; }
    public string? SenderEmail { get; set; }
    public string? SenderAddressType { get; set; }
    public string? SmtpSender { get; set; }
    public DateTime? ReceivedTime { get; set; }
    public DateTime? SentTime { get; set; }
    public string? BodyPlain { get; set; }
    public string? BodyHtml { get; set; }
    public byte[]? BodyRtfCompressed { get; set; }
    public string? BodyRtf { get; set; }
    public string? MessageClass { get; set; }
    public string? InternetMessageId { get; set; }
    public string? DisplayTo { get; set; }
    public string? DisplayCc { get; set; }
    public string? DisplayBcc { get; set; }
    public string? ConversationTopic { get; set; }
    public string? NormalizedSubject { get; set; }
    public int? Importance { get; set; }
    public int? Sensitivity { get; set; }
    public int? MessageFlags { get; set; }

    public List<MsgRecipient> Recipients { get; } = new();
    public List<MsgAttachment> Attachments { get; } = new();
    public IReadOnlyDictionary<uint, MsgProperty> Properties => _properties;

    private readonly Dictionary<uint, MsgProperty> _properties = new();

    internal void SetProperties(Dictionary<uint, MsgProperty> properties)
    {
        _properties.Clear();
        foreach (var p in properties)
            _properties[p.Key] = p.Value;
    }
}

public sealed class MsgAttachment
{
    public string? FileName { get; set; }
    public string? LongFileName { get; internal set; }
    public string? DisplayName { get; internal set; }
    public string? MimeType { get; internal set; }
    public string? Extension { get; internal set; }
    public string? PathName { get; internal set; }
    public int Method { get; internal set; }
    /// <summary>Size of the data in bytes (stream length, known even when the data is not loaded).</summary>
    public long? Size { get; internal set; }
    public int RenderingPosition { get; internal set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public MsgMessage? EmbeddedMessage { get; internal set; }

    public bool IsEmbeddedMessage => Method == 5 && EmbeddedMessage != null;
    public bool IsCustomStorage => Method == 6;
}

public sealed class MsgReaderOptions
{
    public int MaxEmbeddedMessageDepth { get; init; } = 16;
    public int MaxRecipientsPerMessage { get; init; } = 2048;
    public int MaxAttachmentsPerMessage { get; init; } = 2048;
    public long MaxStreamBytes { get; init; } = 64L * 1024 * 1024;
    public bool ReadAllProperties { get; init; } = true;
    /// <summary>When false, attachment bytes are not loaded (only names and sizes). Saves memory when only listing.</summary>
    public bool ReadAttachmentData { get; init; } = true;
    public bool ReadCustomAttachmentData { get; init; } = false;
    public bool Strict { get; init; } = false;
}
