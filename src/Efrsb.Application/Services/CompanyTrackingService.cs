using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
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
    private const int PublicPublicationPageSize = 1;
    private const int MaxPublicPublicationCount = 120;

    private readonly IEfrsbDbContext _db;
    private readonly IFedresursClient _fedresurs;
    private readonly string _filesRoot;

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

    public async Task<TrackedCompanyDto> AddCompanyAsync(
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

    public async Task DeleteCompanyAsync(
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

    public async Task<IReadOnlyList<TrackedCompanyDto>> GetCompaniesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var companies = await _db.TrackedCompanies
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var identityUpdated = false;
        foreach (var company in companies)
            identityUpdated |= await TryRefreshCompanyIdentityAsync(company, cancellationToken);

        if (identityUpdated)
            await _db.SaveChangesAsync(cancellationToken);

        var result = new List<TrackedCompanyDto>();
        foreach (var company in companies)
            result.Add(await ToDtoAsync(company, userId, cancellationToken));

        return result;
    }

    public async Task<int> SyncCompanyAsync(
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

    public async Task<int> SyncCompanyHistoryAsync(
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

            // Публичная карточка fedresurs.ru содержит не только bankruptmessages/reports,
            // но и sfactmessages/ЕГРЮЛ-сообщения. Они могут быть старше первой
            // банкротной публикации, поэтому для истории грузим весь публичный список отдельно.
            totalLoaded += await SyncPublicCompanyPublicationsFullAsync(
                userId,
                company,
                cancellationToken);

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

    public async Task<IReadOnlyList<EfrsbMessageDto>> GetMessagesAsync(
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

    public async Task<EfrsbMessageDetailsDto?> GetMessageDetailsAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.EfrsbMessages
            .Include(x => x.Files)
            .Include(x => x.UserStates)
            .FirstOrDefaultAsync(
                x => x.Id == messageId && x.TrackedCompany!.UserId == userId,
                cancellationToken);

        if (message is null)
            return null;

        var isRead = message.UserStates
            .FirstOrDefault(x => x.UserId == userId)?.IsRead ?? false;

        return new EfrsbMessageDetailsDto(
            message.Id,
            message.FedresursGuid,
            message.Number,
            message.DatePublish,
            message.Type,
            message.ContentXml,
            isRead,
            message.Files
                .Select(f => new MessageFileDto(
                    f.Id,
                    f.FileName,
                    f.SizeBytes))
                .ToList(),
            message.CourtDecisionType);
    }

    public async Task MarkMessageReadAsync(
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
        loaded += await SyncPublicCompanyPublicationsRangeAsync(userId, company, from, to, cancellationToken);
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
                await _db.SaveChangesAsync(cancellationToken);

                await SaveFilesArchiveAsync(message, cancellationToken);

                _db.UserMessageStates.Add(new UserMessageState
                {
                    UserId = userId,
                    EfrsbMessageId = message.Id,
                    IsRead = false
                });

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
                await _db.SaveChangesAsync(cancellationToken);

                await SaveFilesArchiveAsync(report, cancellationToken);

                _db.UserMessageStates.Add(new UserMessageState
                {
                    UserId = userId,
                    EfrsbMessageId = report.Id,
                    IsRead = false
                });

                loaded++;
            }

            if (offset + page.PageData.Count >= page.Total || page.PageData.Count == 0)
                break;

            offset += page.PageData.Count;
        }

        return loaded;
    }


    private async Task<int> SyncPublicCompanyPublicationsRangeAsync(
        Guid userId,
        TrackedCompany company,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var publicCompany = await ResolvePublicCompanyAsync(company, cancellationToken);
        if (publicCompany is null)
            return 0;

        var loaded = 0;
        var offset = 0;

        // Не используем startDate/endDate у публичного backend fedresurs.ru:
        // на части серверов эти запросы возвращают 451. Берём страницы без дат
        // и фильтруем период на нашей стороне.
        while (true)
        {
            var page = await _fedresurs.GetPublicCompanyPublicationsAsync(
                publicCompany.Guid,
                null,
                null,
                PublicPublicationPageSize,
                offset,
                cancellationToken);

            if (page.PageData.Count == 0)
                break;

            if (page.Count > MaxPublicPublicationCount)
                break;

            var periodItems = page.PageData
                .Where(x =>
                {
                    var date = NormalizeFedresursDate(x.DatePublish);
                    return date >= from && date <= to;
                })
                .ToList();

            loaded += await ProcessPublicPublicationPageAsync(
                userId,
                company,
                periodItems,
                cancellationToken);

            var oldestInPage = page.PageData
                .Select(x => NormalizeFedresursDate(x.DatePublish))
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();

            if (oldestInPage < from || offset + page.PageData.Count >= page.Count)
                break;

            if (offset + page.PageData.Count >= MaxPublicPublicationCount)
                break;

            offset += page.PageData.Count;
        }

        return loaded;
    }

    private async Task<int> SyncPublicCompanyPublicationsFullAsync(
        Guid userId,
        TrackedCompany company,
        CancellationToken cancellationToken)
    {
        var publicCompany = await ResolvePublicCompanyAsync(company, cancellationToken);
        if (publicCompany is null)
            return 0;

        var loaded = 0;
        var offset = 0;

        // Историю публичной карточки забираем обычной пагинацией без дат.
        // Запросы с startDate/endDate и большие limit могут отдавать 451.
        while (true)
        {
            var page = await _fedresurs.GetPublicCompanyPublicationsAsync(
                publicCompany.Guid,
                null,
                null,
                PublicPublicationPageSize,
                offset,
                cancellationToken);

            if (page.PageData.Count == 0)
                break;

            if (page.Count > MaxPublicPublicationCount)
                break;

            loaded += await ProcessPublicPublicationPageAsync(
                userId,
                company,
                page.PageData,
                cancellationToken);

            if (offset + page.PageData.Count >= page.Count)
                break;

            if (offset + page.PageData.Count >= MaxPublicPublicationCount)
                break;

            offset += page.PageData.Count;
        }

        return loaded;
    }

    private async Task<int> SyncPublicCompanyPublicationsByDateChunksAsync(
        Guid userId,
        TrackedCompany company,
        string publicCompanyGuid,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var loaded = 0;
        var cursor = from;

        while (cursor < to)
        {
            var chunkTo = cursor.AddDays(31);
            if (chunkTo > to)
                chunkTo = to;

            var offset = 0;
            while (true)
            {
                var page = await _fedresurs.GetPublicCompanyPublicationsAsync(
                    publicCompanyGuid,
                    cursor,
                    chunkTo,
                    PublicPublicationPageSize,
                    offset,
                    cancellationToken);

                loaded += await ProcessPublicPublicationPageAsync(
                    userId,
                    company,
                    page.PageData,
                    cancellationToken);

                if (page.PageData.Count == 0 || offset + page.PageData.Count >= page.Count)
                    break;

                offset += page.PageData.Count;
            }

            cursor = chunkTo;
        }

        return loaded;
    }

    private async Task<int> ProcessPublicPublicationPageAsync(
        Guid userId,
        TrackedCompany company,
        IReadOnlyList<FedresursPublicPublicationItem> items,
        CancellationToken cancellationToken)
    {
        var loaded = 0;

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Guid))
                continue;

            var publicationGuid = item.Guid.Trim();
            var normalizedGuid = publicationGuid.ToLowerInvariant();
            var publicationNumber = string.IsNullOrWhiteSpace(item.Number) ? publicationGuid : item.Number.Trim();
            var publicationDate = NormalizeFedresursDate(item.DatePublish);

            var exists = await _db.EfrsbMessages.AnyAsync(
                x => x.TrackedCompanyId == company.Id
                    && (x.FedresursGuid.ToLower() == normalizedGuid || x.Number == publicationNumber),
                cancellationToken);

            if (exists)
                continue;

            var message = new EfrsbMessage
            {
                TrackedCompanyId = company.Id,
                FedresursGuid = normalizedGuid,
                BankruptGuid = company.BankruptGuid,
                Number = publicationNumber,
                DatePublish = publicationDate,
                Type = BuildPublicType(item.Type),
                CourtDecisionType = item.Title,
                ContentXml = BuildPublicContentXml(item),
                HasViolation = false,
                IsLocked = item.IsLocked,
                LockReason = item.IsLocked ? "Публикация заблокирована на публичном Федресурсе" : null,
                IsAnnulled = item.IsAnnulled
            };

            _db.EfrsbMessages.Add(message);
            await _db.SaveChangesAsync(cancellationToken);

            _db.UserMessageStates.Add(new UserMessageState
            {
                UserId = userId,
                EfrsbMessageId = message.Id,
                IsRead = false
            });

            loaded++;
        }

        return loaded;
    }

    private async Task<FedresursPublicCompanyItem?> ResolvePublicCompanyAsync(
        TrackedCompany company,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _fedresurs.FindPublicCompanyAsync(
                company.Inn,
                company.Ogrn,
                company.Name,
                cancellationToken);
        }
        catch
        {
            return null;
        }
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

        // Метаданные истории берём только из официального service_rest.
        // Публичная карточка Федресурса используется ниже только для дозагрузки
        // sfactmessages/ЕГРЮЛ-публикаций. Если использовать её count/oldest date
        // на этапе metadata, ошибочно выбранная публичная карточка может увести
        // историю в 2015 год и запустить огромную догрузку.

        company.TotalMessages = Math.Max(company.TotalMessages, total);
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
        var unread = await _db.UserMessageStates
            .CountAsync(
                x => x.UserId == userId && !x.IsRead && x.Message!.TrackedCompanyId == company.Id,
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
            if (IsPublicType(message.Type))
                return;

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


    private static string BuildPublicType(string? type)
    {
        return string.IsNullOrWhiteSpace(type)
            ? "Public:Publication"
            : $"Public:{type.Trim()}";
    }

    private static string BuildPublicContentXml(FedresursPublicPublicationItem item)
    {
        var root = new XElement(
            "PublicFedresursPublication",
            new XElement("Guid", item.Guid),
            new XElement("Number", item.Number),
            new XElement("DatePublish", item.DatePublish.ToString("O")),
            new XElement("Type", item.Type ?? string.Empty),
            new XElement("Title", item.Title ?? string.Empty),
            new XElement("PublisherName", item.PublisherName ?? string.Empty),
            new XElement("PublisherType", item.PublisherType ?? string.Empty),
            new XElement("BankruptName", item.BankruptName ?? string.Empty));

        AppendPublicParticipants(root, item.Participants);

        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    private static void AppendPublicParticipants(XElement root, JsonElement? participants)
    {
        if (!participants.HasValue || participants.Value.ValueKind != JsonValueKind.Array)
            return;

        var container = new XElement("Participants");
        foreach (var participant in participants.Value.EnumerateArray())
        {
            var value = participant.ValueKind switch
            {
                JsonValueKind.String => participant.GetString(),
                JsonValueKind.Object => FirstJsonString(
                    participant,
                    "name",
                    "fullName",
                    "shortName",
                    "inn",
                    "ogrn"),
                _ => participant.ToString()
            };

            if (!string.IsNullOrWhiteSpace(value))
                container.Add(new XElement("Participant", value.Trim()));
        }

        if (container.HasElements)
            root.Add(container);
    }

    private static string? FirstJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }

        return null;
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

    private static bool IsPublicType(string type)
    {
        return type.StartsWith("Public:", StringComparison.OrdinalIgnoreCase);
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
