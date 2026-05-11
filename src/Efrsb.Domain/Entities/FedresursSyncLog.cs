namespace Efrsb.Domain.Entities;

public sealed class FedresursSyncLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TrackedCompanyId { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int MessagesLoaded { get; set; }
}
