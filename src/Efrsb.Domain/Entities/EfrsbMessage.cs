namespace Efrsb.Domain.Entities;

public sealed class EfrsbMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TrackedCompanyId { get; set; }
    public TrackedCompany? TrackedCompany { get; set; }
    public string FedresursGuid { get; set; } = string.Empty;
    public string? BankruptGuid { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime DatePublish { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? CourtDecisionType { get; set; }
    public bool? HasViolation { get; set; }
    public bool IsAnnulled { get; set; }
    public bool IsLocked { get; set; }
    public string? LockReason { get; set; }
    public string? ContentXml { get; set; }
    public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<EfrsbMessageFile> Files { get; set; } = new List<EfrsbMessageFile>();
    public ICollection<UserMessageState> UserStates { get; set; } = new List<UserMessageState>();
}
