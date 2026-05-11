using Efrsb.Application.Services;
using Efrsb.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Efrsb.Infrastructure.Data;

public sealed class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IEfrsbDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<TrackedCompany> TrackedCompanies => Set<TrackedCompany>();
    public DbSet<EfrsbMessage> EfrsbMessages => Set<EfrsbMessage>();
    public DbSet<EfrsbMessageFile> EfrsbMessageFiles => Set<EfrsbMessageFile>();
    public DbSet<UserMessageState> UserMessageStates => Set<UserMessageState>();
    public DbSet<FedresursSyncLog> FedresursSyncLogs => Set<FedresursSyncLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<TrackedCompany>()
            .HasIndex(x => new { x.UserId, x.BankruptGuid });

        builder.Entity<EfrsbMessage>()
            .HasIndex(x => new { x.TrackedCompanyId, x.FedresursGuid })
            .IsUnique();

        builder.Entity<UserMessageState>()
            .HasIndex(x => new { x.UserId, x.EfrsbMessageId })
            .IsUnique();
    }
}
