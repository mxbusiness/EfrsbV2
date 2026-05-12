namespace Efrsb.Domain.Entities;

public sealed class TrackedCompany
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public string SearchQuery { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Inn { get; set; }

    public string? Ogrn { get; set; }

    public string? BankruptGuid { get; set; }

    public int TotalMessages { get; set; }

    public int LoadedMessages { get; set; }

    public DateTime? FirstMessageDate { get; set; }

    public DateTime? LastMessageDate { get; set; }

    public DateTime? LastMetadataSyncAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSyncedAtUtc { get; set; }

    public ICollection<EfrsbMessage> Messages { get; set; } = new List<EfrsbMessage>();
}