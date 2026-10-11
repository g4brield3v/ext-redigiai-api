using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Billing;
using RedigiAI.Application.Catalog;

namespace RedigiAI.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<LegalOptions>();

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IClerkWebhookService, ClerkWebhookService>();
        services.AddScoped<ITestAccountService, TestAccountService>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<ICatalogSeedService, CatalogSeedService>();

        return services;
    }
}
