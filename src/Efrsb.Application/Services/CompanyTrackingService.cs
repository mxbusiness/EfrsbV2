using Efrsb.Application.Abstractions;
using Efrsb.Contracts.Companies;
using Efrsb.Contracts.Messages;
using Efrsb.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

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

        var log = new FedresursSyncLog
        {
            TrackedCompanyId = company.Id
        };

        _db.FedresursSyncLogs.Add(log);

        try
        {
            await RefreshMetadataAsync(company, cancellationToken);

            var from = company.LastSyncedAtUtc?.AddDays(-2) ?? DateTime.UtcNow.AddDays(-31);
            var to = DateTime.UtcNow;
            var loaded = 0;

            loaded += await SyncMessagesRangeAsync(
                userId,
                company,
                from,
                to,
                cancellationToken);

            company.LastSyncedAtUtc = DateTime.UtcNow;
            company.LoadedMessages = await CountLoadedMessagesAsync(company.Id, cancellationToken);

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

        var log = new FedresursSyncLog
        {
            TrackedCompanyId = company.Id
        };

        _db.FedresursSyncLogs.Add(log);

        try
        {
            await RefreshMetadataAsync(company, cancellationToken);

            if (company.FirstMessageDate is null)
            {
                log.Success = true;
                log.MessagesLoaded = 0;
                log.FinishedAtUtc = DateTime.UtcNow;

                await _db.SaveChangesAsync(cancellationToken);

                return 0;
            }

            var totalLoaded = 0;
            var from = company.FirstMessageDate.Value;
            var finalTo = DateTime.UtcNow;

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

                from = to;
            }

            company.LastSyncedAtUtc = DateTime.UtcNow;
            company.LoadedMessages = await CountLoadedMessagesAsync(company.Id, cancellationToken);

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
                x.IsAnnulled))
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
                .ToList());
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
                    LockReason = item.LockReason
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

    private async Task RefreshMetadataAsync(
        TrackedCompany company,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(company.BankruptGuid))
            return;

        var firstPage = await _fedresurs.GetMessagesMetadataAsync(
            company.BankruptGuid,
            "DatePublish:asc",
            cancellationToken);

        var lastPage = await _fedresurs.GetMessagesMetadataAsync(
            company.BankruptGuid,
            "DatePublish:desc",
            cancellationToken);

        company.TotalMessages = lastPage.Total;
        company.FirstMessageDate = NormalizeNullableFedresursDate(
            firstPage.PageData.FirstOrDefault()?.DatePublish);

        company.LastMessageDate = NormalizeNullableFedresursDate(
            lastPage.PageData.FirstOrDefault()?.DatePublish);

        company.LastMetadataSyncAtUtc = DateTime.UtcNow;
    }

    private async Task<TrackedCompanyDto> ToDtoAsync(
        TrackedCompany company,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var unread = await _db.UserMessageStates
            .CountAsync(
                x => x.UserId == userId
                     && !x.IsRead
                     && x.Message!.TrackedCompanyId == company.Id,
                cancellationToken);

        var loaded = await CountLoadedMessagesAsync(company.Id, cancellationToken);

        company.LoadedMessages = loaded;

        return new TrackedCompanyDto(
            company.Id,
            company.SearchQuery,
            company.Name,
            company.Inn,
            company.Ogrn,
            company.BankruptGuid,
            unread,
            company.LastSyncedAtUtc,
            company.TotalMessages,
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
            var archiveBytes = await _fedresurs.DownloadMessageFilesArchiveAsync(
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
        return value.HasValue
            ? NormalizeFedresursDate(value.Value)
            : null;
    }
}