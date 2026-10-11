using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Infrastructure.Clerk;
using RedigiAI.Infrastructure.Persistence;
using RedigiAI.Infrastructure.Persistence.Repositories;

namespace RedigiAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDatabaseAdmin, DatabaseAdmin>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IAccessLog, AccessLog>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ICreditRepository, CreditRepository>();
        services.AddScoped<IExamRepository, ExamRepository>();
        services.AddScoped<IEssayTypeRepository, EssayTypeRepository>();
        services.AddScoped<IRubricRepository, RubricRepository>();
        services.AddScoped<IThemeRepository, ThemeRepository>();

        services.AddScoped<IClerkWebhookVerifier, ClerkWebhookVerifier>();

        return services;
    }
}
