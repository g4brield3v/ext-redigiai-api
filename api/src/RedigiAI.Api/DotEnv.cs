namespace RedigiAI.Api;

/// <summary>
/// Desenvolvimento local: carrega o arquivo <c>.env</c> da raiz do repositório como variáveis de ambiente.
/// Variáveis já definidas no ambiente têm prioridade. Em produção o arquivo não existe.
/// </summary>
internal static class DotEnv
{
    public static void LoadIfPresent()
    {
        var path = Find(Directory.GetCurrentDirectory());
        if (path is null)
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = Unquote(trimmed[(separator + 1)..].Trim());
            if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// <c>CHAVE="valor"</c> e <c>CHAVE='valor'</c> valem como <c>CHAVE=valor</c>, como nos outros leitores de .env: as aspas
    /// não fazem parte do valor (um token com aspas é recusado pelo serviço).
    /// </summary>
    internal static string Unquote(string value) =>
        value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private static string? Find(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Não sobe além da raiz do repositório.
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
