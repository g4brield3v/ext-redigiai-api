namespace RedigiAI.Infrastructure.Clerk;

public sealed class ClerkOptions
{
    public const string Section = "Clerk";

    public string Issuer { get; set; } = "";
    public string JwksUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";

    /// <summary>Chave secreta da instância, para a Backend API do Clerk (consulta do e-mail verificado).</summary>
    public string SecretKey { get; set; } = "";

    public string BackendApiUrl { get; set; } = "https://api.clerk.com";

    /// <summary>Opcional: exigido no <c>aud</c> quando o token de sessão do Clerk for configurado com audiência.</summary>
    public string? Audience { get; set; }
}
