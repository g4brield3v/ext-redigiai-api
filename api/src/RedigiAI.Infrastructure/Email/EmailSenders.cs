using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Infrastructure.Email;

public sealed class SendGridOptions
{
    public const string Section = "SendGrid";

    public string ApiKey { get; set; } = "";
    public string From { get; set; } = "correcoes@redigiai.com.br";
    public string FromName { get; set; } = "RedigiAI";

    /// <summary>Só em desenvolvimento, sem chave: pasta onde os e-mails são gravados como HTML em vez de enviados.</summary>
    public string OutboxPath { get; set; } = ".outbox";
}

public static class EmailRegistration
{
    /// <summary>
    /// Com chave, envia pelo SendGrid. Sem chave, só em desenvolvimento: grava cada e-mail como arquivo na pasta
    /// local. Fora de desenvolvimento, sem chave, a validação recusa a configuração; quem sobe a API ou o worker
    /// confere ao iniciar (ver <c>StartupChecks</c>).
    /// </summary>
    /// <remarks>Quem chama liga a seção <see cref="SendGridOptions.Section"/> da configuração.</remarks>
    public static IServiceCollection AddEmailSender(this IServiceCollection services, bool isDevelopment)
    {
        services.AddOptions<SendGridOptions>()
            .Validate(sendGrid => isDevelopment || sendGrid.ApiKey.Length > 0, "SendGrid__ApiKey é obrigatória fora do ambiente Development.");
        services.AddSingleton<SendGridEmailSender>();
        services.AddSingleton<OutboxEmailSender>();
        services.AddSingleton<IEmailSender>(provider =>
            provider.GetRequiredService<IOptions<SendGridOptions>>().Value.ApiKey.Length > 0
                ? provider.GetRequiredService<SendGridEmailSender>()
                : provider.GetRequiredService<OutboxEmailSender>());

        return services;
    }
}

/// <summary>Envio pela API v3 do SendGrid, por HTTP direto (sem SDK).</summary>
internal sealed class SendGridEmailSender(IOptions<SendGridOptions> options, ILogger<SendGridEmailSender> logger) : IEmailSender
{
    private static readonly Uri Endpoint = new("https://api.sendgrid.com/v3/mail/send");

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<string?> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                personalizations = new[] { new { to = new[] { new { email = message.To } } } },
                from = new { email = settings.From, name = settings.FromName },
                subject = message.Subject,
                content = new[]
                {
                    new { type = "text/plain", value = message.Text },
                    new { type = "text/html", value = message.Html },
                },
                // Sem rastreamento de cliques: o link do botão chega ao aluno como escrevemos.
                tracking_settings = new { click_tracking = new { enable = false, enable_text = false } },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await Http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null;
        }

        // Sem o corpo no log: a resposta de erro pode repetir o endereço do aluno.
        logger.LogWarning(
            "SendGrid refused {Template} for {RefId} with status {Status}",
            message.Template,
            message.RefId,
            (int)response.StatusCode);

        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"SendGrid unavailable ({(int)response.StatusCode}).");
        }

        // Pedido recusado (chave inválida, remetente não verificado, endereço inválido): repetir não adianta.
        throw new AppError("EMAIL_REJECTED", 500, "Algo deu errado do nosso lado. Tente novamente em instantes.");
    }
}

/// <summary>
/// Desenvolvimento sem chave do SendGrid: cada e-mail vira um arquivo HTML na pasta local, para abrir no navegador.
/// Nada sai da máquina.
/// </summary>
internal sealed class OutboxEmailSender(IOptions<SendGridOptions> options, ILogger<OutboxEmailSender> logger) : IEmailSender
{
    public async Task<string?> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var folder = Path.GetFullPath(options.Value.OutboxPath);
        Directory.CreateDirectory(folder);

        var id = $"{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{message.Template}-{message.RefId:N}";
        var header = $"<!-- Para: {WebUtility.HtmlEncode(message.To)} | Assunto: {WebUtility.HtmlEncode(message.Subject)} -->\n";
        await File.WriteAllTextAsync(Path.Combine(folder, id + ".html"), header + message.Html, Encoding.UTF8, ct);

        logger.LogInformation("E-mail {Template} for {RefId} written to the local outbox", message.Template, message.RefId);
        return $"outbox:{id}";
    }
}
