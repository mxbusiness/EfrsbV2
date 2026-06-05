using Efrsb.Application.Abstractions;
using Efrsb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Efrsb.Infrastructure.Jobs;

public sealed class FedresursSyncJob
{
    private readonly AppDbContext _db;
    private readonly ICompanyTrackingService _tracking;
    private readonly ILogger<FedresursSyncJob> _logger;

    public FedresursSyncJob(
        AppDbContext db,
        ICompanyTrackingService tracking,
        ILogger<FedresursSyncJob> logger)
    {
        _db = db;
        _tracking = tracking;
        _logger = logger;
    }

    public async Task SyncCompanyAsync(Guid userId, Guid companyId)
    {
        try
        {
            await _tracking.SyncCompanyAsync(userId, companyId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fedresurs company sync failed for {CompanyId}", companyId);
            throw;
        }
    }

    public async Task SyncCompanyHistoryAsync(Guid userId, Guid companyId)
    {
        try
        {
            await _tracking.SyncCompanyHistoryAsync(userId, companyId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fedresurs company history sync failed for {CompanyId}", companyId);
            throw;
        }
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var pairs = await _db.TrackedCompanies
            .Select(x => new { x.UserId, CompanyId = x.Id })
            .ToListAsync(cancellationToken);

        foreach (var pair in pairs)
        {
            try
            {
                await _tracking.SyncCompanyAsync(
                    pair.UserId,
                    pair.CompanyId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fedresurs sync failed for {CompanyId}", pair.CompanyId);
            }
        }
    }
}
