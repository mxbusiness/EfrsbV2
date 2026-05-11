using Efrsb.Contracts.Fedresurs;

namespace Efrsb.Application.Abstractions;

public interface IFedresursClient
{
    Task<FedresursPagedResponse<FedresursBankruptItem>> SearchBankruptsAsync(string query, CancellationToken cancellationToken = default);
    Task<FedresursPagedResponse<FedresursMessageItem>> GetMessagesAsync(string? bankruptGuid, DateTime dateFrom, DateTime dateTo, int limit = 1000, int offset = 0, CancellationToken cancellationToken = default);
    Task<FedresursMessageItem?> GetMessageAsync(string guid, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadMessageFilesArchiveAsync(string guid, bool onlySafe = true, CancellationToken cancellationToken = default);
}
