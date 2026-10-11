using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Accounts;

namespace RedigiAI.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<LegalOptions>();

        services.AddScoped<RegisterUserHandler>();
        services.AddScoped<GetProfileHandler>();
        services.AddScoped<UpdateProfileHandler>();
        services.AddScoped<SyncClerkEmailHandler>();

        return services;
    }
}
