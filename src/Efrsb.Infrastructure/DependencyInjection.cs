using Efrsb.Application.Abstractions;
using Efrsb.Application.Services;
using Efrsb.Domain.Entities;
using Efrsb.Infrastructure.Data;
using Efrsb.Infrastructure.Fedresurs;
using Efrsb.Infrastructure.Jobs;
using Efrsb.Infrastructure.Options;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Efrsb.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEfrsbInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        services.Configure<FedresursOptions>(configuration.GetSection("Fedresurs"));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IEfrsbDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.AddHttpClient<IFedresursClient, FedresursClient>();
        services.AddScoped<ICompanyTrackingService, CompanyTrackingService>();
        services.AddScoped<FedresursSyncJob>();

        services.AddHangfire(config => config.UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer();
        return services;
    }
}
