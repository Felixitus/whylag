namespace WhyLag.AI;

/// <summary>A model WhyLag has been tested with. Sizes are approximate download sizes of the default tag.</summary>
public sealed record ModelSuggestion(string Name, double DownloadGb, double MinMemoryGb, string License, string Note);

public static class ModelAdvisor
{
    /// <summary>Ordered from preferred to fallback. Licenses belong to the model authors, not to WhyLag.</summary>
    public static IReadOnlyList<ModelSuggestion> Catalog { get; } =
    [
        new("qwen3:8b", 5.2, 8, "Apache-2.0", "model.note.8b"),
        new("qwen3:4b", 2.5, 4, "Apache-2.0", "model.note.4b"),
        new("phi4-mini", 2.5, 4, "MIT", "model.note.alt"),
        new("gemma3:4b", 3.3, 5, "Gemma Terms of Use", "model.note.license"),
        new("llama3.2:3b", 2.0, 4, "Llama 3.2 Community License", "model.note.license"),
        new("qwen3:1.7b", 1.4, 2, "Apache-2.0", "model.note.small"),
    ];

    /// <summary>Suggests a model for this PC. Returns null when AI explanations are not recommended.</summary>
    public static ModelSuggestion? Recommend(double vramMb, double ramMb)
    {
        var vramGb = vramMb / 1024;
        var ramGb = ramMb / 1024;
        if (vramGb >= 8) return Find("qwen3:8b");
        if (vramGb >= 4 || ramGb >= 16) return Find("qwen3:4b");
        if (ramGb >= 8) return Find("qwen3:1.7b");
        return null;
    }

    /// <summary>The best installed local model: a catalog model if present, otherwise the smallest local chat model.</summary>
    public static string? PickInstalled(IReadOnlyList<OllamaModel> installed)
    {
        var local = installed.Where(m => !IsCloudModel(m.Name)).ToList();
        foreach (var suggestion in Catalog)
        {
            var match = local.FirstOrDefault(m => SameModel(m.Name, suggestion.Name));
            if (match is not null) return match.Name;
        }
        return local
            .Where(m => !m.Name.Contains("embed", StringComparison.OrdinalIgnoreCase)
                        && m.Family?.Contains("bert", StringComparison.OrdinalIgnoreCase) != true)
            .OrderBy(m => m.SizeBytes)
            .Select(m => m.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// Ollama cloud models ("model:cloud", "model:20b-cloud") run on remote servers. WhyLag never uses
    /// them, because incident data would leave the PC.
    /// </summary>
    public static bool IsCloudModel(string name)
    {
        var tag = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : "";
        return tag.Equals("cloud", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"qwen3" and "qwen3:latest" name the same model in Ollama.</summary>
    public static bool SameModel(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => name.Contains(':') ? name : name + ":latest";

    private static ModelSuggestion Find(string name) => Catalog.First(c => c.Name == name);
}
