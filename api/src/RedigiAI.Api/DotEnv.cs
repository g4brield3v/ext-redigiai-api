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
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

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
