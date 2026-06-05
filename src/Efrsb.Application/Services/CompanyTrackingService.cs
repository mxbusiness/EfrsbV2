using System.Collections.Concurrent;
using System.IO.Compression;
using Efrsb.Application.Abstractions;
using Efrsb.Contracts.Companies;
using Efrsb.Contracts.Fedresurs;
using Efrsb.Contracts.Messages;
using Efrsb.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Efrsb.Application.Services;

public interface IEfrsbDbContext
{
    DbSet<TrackedCompany> TrackedCompanies { get; }
    DbSet<EfrsbMessage> EfrsbMessages { get; }
    DbSet<EfrsbMessageFile> EfrsbMessageFiles { get; }
    DbSet<UserMessageState> UserMessageStates { get; }
    DbSet<FedresursSyncLog> FedresursSyncLogs { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class CompanyTrackingService : ICompanyTrackingService
{
    private readonly IEfrsbDbContext _db;
    private readonly IFedresursClient _fedresurs;
    private readonly string _filesRoot;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CompanySyncLocks = new();

    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public CompanyTrackingService(
        IEfrsbDbContext db,
        IFedresursClient fedresurs)
    {
        _db = db;
        _fedresurs = fedresurs;
        _filesRoot = Path.Combine(
            AppContext.BaseDirectory,
            "storage",
            "fedresurs-files");

        Directory.CreateDirectory(_filesRoot);
    }

    public Task<TrackedCompanyDto> AddCompanyAsync(
        Guid userId,
        string query,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => AddCompanyCoreAsync(userId, query, cancellationToken),
            cancellationToken);
    }

    public Task DeleteCompanyAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => DeleteCompanyCoreAsync(userId, trackedCompanyId, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<TrackedCompanyDto>> GetCompaniesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => GetCompaniesCoreAsync(userId, cancellationToken),
            cancellationToken);
    }

    public Task<int> SyncCompanyAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return RunCompanySyncSerializedAsync(
            trackedCompanyId,
            () => SyncCompanyCoreAsync(userId, trackedCompanyId, cancellationToken),
            cancellationToken);
    }

    public Task<int> SyncCompanyHistoryAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return RunCompanySyncSerializedAsync(
            trackedCompanyId,
            () => SyncCompanyHistoryCoreAsync(userId, trackedCompanyId, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<EfrsbMessageDto>> GetMessagesAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => GetMessagesCoreAsync(userId, trackedCompanyId, cancellationToken),
            cancellationToken);
    }

    public Task<EfrsbMessageDetailsDto?> GetMessageDetailsAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => GetMessageDetailsCoreAsync(userId, messageId, cancellationToken),
            cancellationToken);
    }

    public Task MarkMessageReadAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => MarkMessageReadCoreAsync(userId, messageId, cancellationToken),
            cancellationToken);
    }

    public Task<int> MarkCompanyMessagesReadAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => MarkCompanyMessagesReadCoreAsync(userId, trackedCompanyId, cancellationToken),
            cancellationToken);
    }

    private static async Task<T> RunCompanySyncSerializedAsync<T>(
        Guid trackedCompanyId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = CompanySyncLocks.GetOrAdd(trackedCompanyId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RunSerializedAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<TrackedCompanyDto> AddCompanyCoreAsync(
        Guid userId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            throw new InvalidOperationException("Введите ИНН, ОГРН, название или GUID компании.");

        var bankrupts = await _fedresurs.SearchBankruptsAsync(
            normalizedQuery,
            cancellationToken);

        var first = bankrupts.PageData.FirstOrDefault();
        if (first is null || string.IsNullOrWhiteSpace(first.Guid))
            throw new InvalidOperationException("Компания не найдена в ЕФРСБ.");

        var alreadyExists = await _db.TrackedCompanies
            .AnyAsync(
                x => x.UserId == userId && x.BankruptGuid == first.Guid,
                cancellationToken);

        if (alreadyExists)
            throw new InvalidOperationException("Эта компания уже добавлена в отслеживание.");

        var entity = new TrackedCompany
        {
            UserId = userId,
            SearchQuery = normalizedQuery,
            BankruptGuid = first.Guid,
            Name = first.Name,
            Inn = first.Inn,
            Ogrn = first.Ogrn
        };

        await RefreshMetadataAsync(entity, cancellationToken);

        _db.TrackedCompanies.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(entity, userId, cancellationToken);
    }

    private async Task DeleteCompanyCoreAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        var company = await _db.TrackedCompanies
            .FirstOrDefaultAsync(
                x => x.Id == trackedCompanyId && x.UserId == userId,
                cancellationToken);

        if (company is null)
            return;

        var messageIds = await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == trackedCompanyId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var states = await _db.UserMessageStates
            .Where(x => messageIds.Contains(x.EfrsbMessageId))
            .ToListAsync(cancellationToken);

        var files = await _db.EfrsbMessageFiles
            .Where(x => messageIds.Contains(x.EfrsbMessageId))
            .ToListAsync(cancellationToken);

        var messages = await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == trackedCompanyId)
            .ToListAsync(cancellationToken);

        var logs = await _db.FedresursSyncLogs
            .Where(x => x.TrackedCompanyId == trackedCompanyId)
            .ToListAsync(cancellationToken);

        _db.UserMessageStates.RemoveRange(states);
        _db.EfrsbMessageFiles.RemoveRange(files);
        _db.EfrsbMessages.RemoveRange(messages);
        _db.FedresursSyncLogs.RemoveRange(logs);
        _db.TrackedCompanies.Remove(company);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<TrackedCompanyDto>> GetCompaniesCoreAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var companies = await _db.TrackedCompanies
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var result = new List<TrackedCompanyDto>();
        foreach (var company in companies)
            result.Add(await ToDtoAsync(company, userId, cancellationToken));

        return result;
    }

    private async Task<int> SyncCompanyCoreAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        var company = await _db.TrackedCompanies
            .FirstOrDefaultAsync(
                x => x.Id == trackedCompanyId && x.UserId == userId,
                cancellationToken)
            ?? throw new InvalidOperationException("Tracked company not found.");

        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            throw new InvalidOperationException("Company has no BankruptGuid.");

        var log = new FedresursSyncLog { TrackedCompanyId = company.Id };
        _db.FedresursSyncLogs.Add(log);

        try
        {
            await TryRefreshCompanyIdentityAsync(company, cancellationToken);
            await RemoveNonServiceRestMessagesAsync(company.Id, cancellationToken);
            await RefreshMetadataAsync(company, cancellationToken);

            var from = company.LastSyncedAtUtc?.AddDays(-2) ?? DateTime.UtcNow.AddDays(-31);
            var to = DateTime.UtcNow;

            var loaded = await SyncPublicationsRangeAsync(
                userId,
                company,
                from,
                to,
                cancellationToken);

            company.LastSyncedAtUtc = DateTime.UtcNow;
            company.LoadedMessages = await CountLoadedMessagesAsync(company.Id, cancellationToken);
            company.TotalMessages = Math.Max(company.TotalMessages, company.LoadedMessages);

            log.Success = true;
            log.MessagesLoaded = loaded;
            log.FinishedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
            return loaded;
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.Error = ex.Message;
            log.FinishedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<int> SyncCompanyHistoryCoreAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        var company = await _db.TrackedCompanies
            .FirstOrDefaultAsync(
                x => x.Id == trackedCompanyId && x.UserId == userId,
                cancellationToken)
            ?? throw new InvalidOperationException("Tracked company not found.");

        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            throw new InvalidOperationException("Company has no BankruptGuid.");

        var log = new FedresursSyncLog { TrackedCompanyId = company.Id };
        _db.FedresursSyncLogs.Add(log);

        try
        {
            await TryRefreshCompanyIdentityAsync(company, cancellationToken);
            await RemoveNonServiceRestMessagesAsync(company.Id, cancellationToken);
            await RefreshMetadataAsync(company, cancellationToken);

            var totalLoaded = 0;
            var finalTo = DateTime.UtcNow;

            if (company.FirstMessageDate is not null)
            {
                var from = company.FirstMessageDate.Value;

                while (from < finalTo)
                {
                    var to = from.AddDays(31);
                    if (to > finalTo)
                        to = finalTo;

                    totalLoaded += await SyncMessagesRangeAsync(
                        userId,
                        company,
                        from,
                        to,
                        cancellationToken);

                    totalLoaded += await SyncReportsRangeAsync(
                        userId,
                        company,
                        from,
                        to,
                        cancellationToken);

                    from = to;
                }
            }

            company.LastSyncedAtUtc = DateTime.UtcNow;
            company.LoadedMessages = await CountLoadedMessagesAsync(company.Id, cancellationToken);
            company.TotalMessages = company.LoadedMessages;

            log.Success = true;
            log.MessagesLoaded = totalLoaded;
            log.FinishedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
            return totalLoaded;
        }
        catch (Exception ex)
        {
            log.Success = false;
            log.Error = ex.Message;
            log.FinishedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlyList<EfrsbMessageDto>> GetMessagesCoreAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        return await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == trackedCompanyId && x.TrackedCompany!.UserId == userId)
            .OrderByDescending(x => x.DatePublish)
            .Select(x => new EfrsbMessageDto(
                x.Id,
                x.FedresursGuid,
                x.Number,
                x.DatePublish,
                x.Type,
                x.UserStates
                    .Where(s => s.UserId == userId)
                    .Select(s => s.IsRead)
                    .FirstOrDefault(),
                x.HasViolation,
                x.IsLocked,
                x.IsAnnulled,
                x.CourtDecisionType))
            .ToListAsync(cancellationToken);
    }

    private async Task<EfrsbMessageDetailsDto?> GetMessageDetailsCoreAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.EfrsbMessages
            .FirstOrDefaultAsync(
                x => x.Id == messageId && x.TrackedCompany!.UserId == userId,
                cancellationToken);

        if (message is null)
            return null;

        var isRead = await _db.UserMessageStates
            .Where(x => x.UserId == userId && x.EfrsbMessageId == messageId)
            .Select(x => x.IsRead)
            .FirstOrDefaultAsync(cancellationToken);

        var files = await _db.EfrsbMessageFiles
            .Where(x => x.EfrsbMessageId == messageId)
            .OrderBy(x => x.FileName)
            .Select(f => new MessageFileDto(
                f.Id,
                f.FileName,
                f.SizeBytes))
            .ToListAsync(cancellationToken);

        return new EfrsbMessageDetailsDto(
            message.Id,
            message.FedresursGuid,
            message.Number,
            message.DatePublish,
            message.Type,
            message.ContentXml,
            isRead,
            files,
            message.CourtDecisionType);
    }

    private async Task MarkMessageReadCoreAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var state = await _db.UserMessageStates
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EfrsbMessageId == messageId,
                cancellationToken);

        if (state is null)
        {
            _db.UserMessageStates.Add(new UserMessageState
            {
                UserId = userId,
                EfrsbMessageId = messageId,
                IsRead = true,
                ReadAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            state.IsRead = true;
            state.ReadAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> MarkCompanyMessagesReadCoreAsync(
        Guid userId,
        Guid trackedCompanyId,
        CancellationToken cancellationToken = default)
    {
        var companyExists = await _db.TrackedCompanies
            .AnyAsync(
                x => x.Id == trackedCompanyId && x.UserId == userId,
                cancellationToken);

        if (!companyExists)
            return 0;

        var messageIds = await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == trackedCompanyId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (messageIds.Count == 0)
            return 0;

        var states = await _db.UserMessageStates
            .Where(x => x.UserId == userId && messageIds.Contains(x.EfrsbMessageId))
            .ToListAsync(cancellationToken);

        var statesByMessageId = states.ToDictionary(x => x.EfrsbMessageId);
        var changed = 0;
        var now = DateTime.UtcNow;

        foreach (var messageId in messageIds)
        {
            if (statesByMessageId.TryGetValue(messageId, out var state))
            {
                if (!state.IsRead)
                {
                    state.IsRead = true;
                    state.ReadAtUtc = now;
                    changed++;
                }
            }
            else
            {
                _db.UserMessageStates.Add(new UserMessageState
                {
                    UserId = userId,
                    EfrsbMessageId = messageId,
                    IsRead = true,
                    ReadAtUtc = now
                });
                changed++;
            }
        }

        if (changed > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return changed;
    }

    private async Task RemoveNonServiceRestMessagesAsync(
        Guid trackedCompanyId,
        CancellationToken cancellationToken)
    {
        var publicMessageIds = await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == trackedCompanyId && x.Type.StartsWith("Public:"))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (publicMessageIds.Count == 0)
            return;

        var states = await _db.UserMessageStates
            .Where(x => publicMessageIds.Contains(x.EfrsbMessageId))
            .ToListAsync(cancellationToken);

        var files = await _db.EfrsbMessageFiles
            .Where(x => publicMessageIds.Contains(x.EfrsbMessageId))
            .ToListAsync(cancellationToken);

        var messages = await _db.EfrsbMessages
            .Where(x => publicMessageIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        _db.UserMessageStates.RemoveRange(states);
        _db.EfrsbMessageFiles.RemoveRange(files);
        _db.EfrsbMessages.RemoveRange(messages);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> SyncPublicationsRangeAsync(
        Guid userId,
        TrackedCompany company,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var loaded = 0;
        loaded += await SyncMessagesRangeAsync(userId, company, from, to, cancellationToken);
        loaded += await SyncReportsRangeAsync(userId, company, from, to, cancellationToken);
        return loaded;
    }

    private async Task<int> SyncMessagesRangeAsync(
        Guid userId,
        TrackedCompany company,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            return 0;

        var loaded = 0;
        var offset = 0;

        while (true)
        {
            var page = await _fedresurs.GetMessagesAsync(
                company.BankruptGuid,
                from,
                to,
                1000,
                offset,
                cancellationToken);

            foreach (var item in page.PageData)
            {
                var exists = await _db.EfrsbMessages.AnyAsync(
                    x => x.FedresursGuid == item.Guid && x.TrackedCompanyId == company.Id,
                    cancellationToken);

                if (exists)
                    continue;

                var details = await _fedresurs.GetMessageAsync(
                    item.Guid,
                    cancellationToken) ?? item;

                var message = new EfrsbMessage
                {
                    TrackedCompanyId = company.Id,
                    FedresursGuid = item.Guid,
                    BankruptGuid = item.BankruptGuid,
                    Number = item.Number,
                    DatePublish = NormalizeFedresursDate(item.DatePublish),
                    Type = item.Type,
                    CourtDecisionType = item.CourtDecisionType,
                    ContentXml = details.Content ?? item.Content,
                    HasViolation = item.HasViolation,
                    IsLocked = !string.IsNullOrWhiteSpace(item.LockReason),
                    LockReason = item.LockReason,
                    IsAnnulled = !string.IsNullOrWhiteSpace(item.AnnulmentMessageGuid)
                };

                _db.EfrsbMessages.Add(message);
                _db.UserMessageStates.Add(new UserMessageState
                {
                    UserId = userId,
                    EfrsbMessageId = message.Id,
                    IsRead = false
                });
                await _db.SaveChangesAsync(cancellationToken);

                await SaveFilesArchiveAsync(message, cancellationToken);

                loaded++;
            }

            if (offset + page.PageData.Count >= page.Total || page.PageData.Count == 0)
                break;

            offset += page.PageData.Count;
        }

        return loaded;
    }

    private async Task<int> SyncReportsRangeAsync(
        Guid userId,
        TrackedCompany company,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            return 0;

        var loaded = 0;
        var offset = 0;

        while (true)
        {
            var page = await _fedresurs.GetReportsAsync(
                company.BankruptGuid,
                from,
                to,
                1000,
                offset,
                cancellationToken);

            foreach (var item in page.PageData)
            {
                var exists = await _db.EfrsbMessages.AnyAsync(
                    x => x.FedresursGuid == item.Guid && x.TrackedCompanyId == company.Id,
                    cancellationToken);

                if (exists)
                    continue;

                var details = await _fedresurs.GetReportAsync(
                    item.Guid,
                    cancellationToken) ?? item;

                var report = new EfrsbMessage
                {
                    TrackedCompanyId = company.Id,
                    FedresursGuid = item.Guid,
                    BankruptGuid = item.BankruptGuid,
                    Number = item.Number,
                    DatePublish = NormalizeFedresursDate(item.DatePublish),
                    Type = BuildReportType(item.Type),
                    CourtDecisionType = item.ProcedureType,
                    ContentXml = details.Content ?? item.Content,
                    HasViolation = false,
                    IsLocked = !string.IsNullOrWhiteSpace(item.LockReason),
                    LockReason = item.LockReason,
                    IsAnnulled = !string.IsNullOrWhiteSpace(item.AnnulmentReportGuid)
                };

                _db.EfrsbMessages.Add(report);
                _db.UserMessageStates.Add(new UserMessageState
                {
                    UserId = userId,
                    EfrsbMessageId = report.Id,
                    IsRead = false
                });
                await _db.SaveChangesAsync(cancellationToken);

                await SaveFilesArchiveAsync(report, cancellationToken);

                loaded++;
            }

            if (offset + page.PageData.Count >= page.Total || page.PageData.Count == 0)
                break;

            offset += page.PageData.Count;
        }

        return loaded;
    }


    private async Task RefreshMetadataAsync(
        TrackedCompany company,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            return;

        var firstMessagesPage = await _fedresurs.GetMessagesMetadataAsync(
            company.BankruptGuid,
            "DatePublish:asc",
            cancellationToken);

        var lastMessagesPage = await _fedresurs.GetMessagesMetadataAsync(
            company.BankruptGuid,
            "DatePublish:desc",
            cancellationToken);

        var firstReportsPage = await _fedresurs.GetReportsMetadataAsync(
            company.BankruptGuid,
            "DatePublish:asc",
            cancellationToken);

        var lastReportsPage = await _fedresurs.GetReportsMetadataAsync(
            company.BankruptGuid,
            "DatePublish:desc",
            cancellationToken);

        var firstDates = new[]
            {
                firstMessagesPage.PageData.FirstOrDefault()?.DatePublish,
                firstReportsPage.PageData.FirstOrDefault()?.DatePublish
            }
            .Where(x => x.HasValue)
            .Select(x => NormalizeFedresursDate(x!.Value))
            .ToList();

        var lastDates = new[]
            {
                lastMessagesPage.PageData.FirstOrDefault()?.DatePublish,
                lastReportsPage.PageData.FirstOrDefault()?.DatePublish
            }
            .Where(x => x.HasValue)
            .Select(x => NormalizeFedresursDate(x!.Value))
            .ToList();

        var total = lastMessagesPage.Total + lastReportsPage.Total;
        company.TotalMessages = total;
        company.FirstMessageDate = firstDates.Count == 0 ? null : firstDates.Min();
        company.LastMessageDate = lastDates.Count == 0 ? null : lastDates.Max();
        company.LastMetadataSyncAtUtc = DateTime.UtcNow;
    }

    private async Task<bool> TryRefreshCompanyIdentityAsync(
        TrackedCompany company,
        CancellationToken cancellationToken)
    {
        if (!NeedsIdentityRefresh(company) || string.IsNullOrWhiteSpace(company.BankruptGuid))
            return false;

        try
        {
            var bankrupts = await _fedresurs.SearchBankruptsAsync(
                company.BankruptGuid,
                cancellationToken);

            var item = bankrupts.PageData.FirstOrDefault(x =>
                    string.Equals(x.Guid, company.BankruptGuid, StringComparison.OrdinalIgnoreCase))
                ?? bankrupts.PageData.FirstOrDefault();

            if (item is null)
                return false;

            var changed = false;

            if (string.IsNullOrWhiteSpace(company.Name) && !string.IsNullOrWhiteSpace(item.Name))
            {
                company.Name = item.Name.Trim();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(company.Inn) && !string.IsNullOrWhiteSpace(item.Inn))
            {
                company.Inn = item.Inn.Trim();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(company.Ogrn) && !string.IsNullOrWhiteSpace(item.Ogrn))
            {
                company.Ogrn = item.Ogrn.Trim();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(company.BankruptGuid) && !string.IsNullOrWhiteSpace(item.Guid))
            {
                company.BankruptGuid = item.Guid;
                changed = true;
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<TrackedCompanyDto> ToDtoAsync(
        TrackedCompany company,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var unread = await _db.EfrsbMessages
            .Where(x => x.TrackedCompanyId == company.Id)
            .CountAsync(
                x => !x.UserStates.Any(s => s.UserId == userId)
                    || x.UserStates.Any(s => s.UserId == userId && !s.IsRead),
                cancellationToken);

        var loaded = await CountLoadedMessagesAsync(company.Id, cancellationToken);
        company.LoadedMessages = loaded;
        var total = Math.Max(company.TotalMessages, loaded);

        return new TrackedCompanyDto(
            company.Id,
            company.SearchQuery,
            company.Name,
            company.Inn,
            company.Ogrn,
            company.BankruptGuid,
            unread,
            company.LastSyncedAtUtc,
            total,
            loaded,
            company.FirstMessageDate,
            company.LastMessageDate,
            company.LastMetadataSyncAtUtc);
    }

    private async Task<int> CountLoadedMessagesAsync(
        Guid trackedCompanyId,
        CancellationToken cancellationToken)
    {
        return await _db.EfrsbMessages
            .CountAsync(
                x => x.TrackedCompanyId == trackedCompanyId,
                cancellationToken);
    }

    private async Task SaveFilesArchiveAsync(
        EfrsbMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var archiveBytes = IsReportType(message.Type)
                ? await _fedresurs.DownloadReportFilesArchiveAsync(
                    message.FedresursGuid,
                    true,
                    cancellationToken)
                : await _fedresurs.DownloadMessageFilesArchiveAsync(
                    message.FedresursGuid,
                    true,
                    cancellationToken);

            if (archiveBytes.Length == 0)
                return;

            var messageDir = Path.Combine(
                _filesRoot,
                message.Id.ToString("N"));

            Directory.CreateDirectory(messageDir);

            var archivePath = Path.Combine(
                messageDir,
                "archive.zip");

            await File.WriteAllBytesAsync(
                archivePath,
                archiveBytes,
                cancellationToken);

            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
            {
                _db.EfrsbMessageFiles.Add(new EfrsbMessageFile
                {
                    EfrsbMessageId = message.Id,
                    FileName = entry.Name,
                    StoragePath = archivePath,
                    SizeBytes = entry.Length
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Файлы не должны ломать синхронизацию сообщения.
        }
    }


    private static string BuildReportType(string type)
    {
        return string.IsNullOrWhiteSpace(type)
            ? "Report"
            : $"Report:{type.Trim()}";
    }

    private static bool IsReportType(string type)
    {
        return type.StartsWith("Report", StringComparison.OrdinalIgnoreCase);
    }


    private static bool NeedsIdentityRefresh(TrackedCompany company)
    {
        return string.IsNullOrWhiteSpace(company.Name)
            || string.IsNullOrWhiteSpace(company.Inn)
            || string.IsNullOrWhiteSpace(company.Ogrn);
    }


    private static DateTime NormalizeFedresursDate(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
            return value;

        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static DateTime? NormalizeNullableFedresursDate(DateTime? value)
    {
        return value.HasValue ? NormalizeFedresursDate(value.Value) : null;
    }
}
