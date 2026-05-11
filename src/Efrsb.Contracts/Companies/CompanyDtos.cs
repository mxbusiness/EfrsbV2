namespace Efrsb.Contracts.Companies;

public sealed record CreateTrackedCompanyRequest(string Query);
public sealed record TrackedCompanyDto(Guid Id, string SearchQuery, string? Name, string? Inn, string? Ogrn, string? BankruptGuid, int UnreadCount, DateTime? LastSyncedAtUtc);
