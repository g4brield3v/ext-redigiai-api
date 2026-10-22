using RedigiAI.Application.Catalog;

namespace RedigiAI.Api;

/// <summary>
/// As propostas que vêm com o programa (pasta <c>Content/Prompts</c>, embutida no executável): uma pasta por proposta,
/// com o <c>prompt.json</c> e as imagens que ele cita.
/// </summary>
internal static class BundledPrompts
{
    private const string Prefix = "prompts/";
    private const string DocumentName = "prompt.json";

    public static IReadOnlyList<PromptPackage> Packages()
    {
        var assembly = typeof(BundledPrompts).Assembly;

        // No Windows o caminho da pasta entra no nome do recurso com barra invertida.
        var files = assembly.GetManifestResourceNames()
            .Select(name => (Resource: name, Path: name.Replace('\\', '/')))
            .Where(entry => entry.Path.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(entry => (entry.Resource, Parts: entry.Path[Prefix.Length..].Split('/', 2)))
            .Where(entry => entry.Parts.Length == 2)
            .GroupBy(entry => entry.Parts[0], StringComparer.Ordinal);

        var packages = new List<PromptPackage>();
        foreach (var folder in files)
        {
            var contents = folder.ToDictionary(entry => entry.Parts[1], entry => Read(assembly, entry.Resource), StringComparer.Ordinal);
            if (contents.Remove(DocumentName, out var document))
            {
                packages.Add(new PromptPackage(folder.Key, System.Text.Encoding.UTF8.GetString(document), contents));
            }
        }

        return packages;
    }

    private static byte[] Read(System.Reflection.Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
