namespace Efrsb.Domain.Entities;

public sealed class EfrsbMessageFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EfrsbMessageId { get; set; }
    public EfrsbMessage? Message { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
}
