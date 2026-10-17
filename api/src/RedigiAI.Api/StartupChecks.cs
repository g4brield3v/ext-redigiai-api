using System.Reflection;
using RedigiAI.Application.Notifications;
using RedigiAI.Infrastructure.Email;

namespace RedigiAI.Api;

/// <summary>Configuração que a API e o worker conferem ao iniciar: faltando, o processo não sobe.</summary>
internal static class StartupChecks
{
    /// <summary>
    /// O build sobe a aplicação para gerar o <c>openapi.json</c>, fora do ambiente Development e sem o .env: ali a
    /// configuração de produção não existe e não é conferida.
    /// </summary>
    public static bool GeneratingOpenApiDocument =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    public static IServiceCollection AddStartupChecks(this IServiceCollection services)
    {
        if (!GeneratingOpenApiDocument)
        {
            services.AddOptions<SiteOptions>().ValidateOnStart();
            services.AddOptions<SendGridOptions>().ValidateOnStart();
        }

        return services;
    }
}
