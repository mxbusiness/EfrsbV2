using Efrsb.Contracts.Companies;
using Efrsb.Contracts.Messages;

namespace Efrsb.Application.Abstractions;

public interface ICompanyTrackingService
{
    Task<TrackedCompanyDto> AddCompanyAsync(
        Guid userId,
        string query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackedCompanyDto>> GetCompaniesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<int> SyncCompanyAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default);

    Task<int> SyncCompanyHistoryAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EfrsbMessageDto>> GetMessagesAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default);

    Task<EfrsbMessageDetailsDto?> GetMessageDetailsAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task MarkMessageReadAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<int> MarkCompanyMessagesReadAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default);

    Task DeleteCompanyAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default);
}
