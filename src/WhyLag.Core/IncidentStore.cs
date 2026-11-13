using System.Text.Json;

namespace WhyLag.Core;

/// <summary>Stores each incident as a readable JSON file so users can inspect, share or delete it.</summary>
public sealed class IncidentStore(string? directory = null)
{
    public string Directory { get; } = directory ?? AppPaths.IncidentsDir;

    /// <summary>The file of an incident. Ids are made of letters, digits, '-' and '_' only; anything else is rejected.</summary>
    public string PathFor(string id) => IsValidId(id)
        ? System.IO.Path.Combine(Directory, $"{id}.json")
        : throw new ArgumentException("Invalid incident id.", nameof(id));

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public void Save(Incident incident)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(incident.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(incident, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public Incident? Load(string id) => IsValidId(id) ? LoadFile(PathFor(id)) : null;

    public static Incident? LoadFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Incident>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Incident ids, newest first. Ids sort chronologically by design.</summary>
    public List<string> ListIds()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderDescending(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>All readable incidents, newest first, with everything that was recorded.</summary>
    public List<Incident> LoadAll() => ListIds().Select(Load).OfType<Incident>().ToList();

    /// <summary>
    /// All readable incidents, newest first, without the recorded data: findings, note and the foreground program of
    /// each second only. Lists and statistics need no more, and this skips most of the work and memory.
    /// </summary>
    public List<Incident> LoadSummaries() => ListIds().AsParallel().AsOrdered().Select(id =>
    {
        try
        {
            using var file = File.OpenRead(PathFor(id));
            return JsonSerializer.Deserialize<Incident>(file, Json.Summary);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }).OfType<Incident>().ToList();

    public void Delete(string id)
    {
        if (!IsValidId(id)) return;
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        SetPinned(id, false);
    }

    private string PinFile => System.IO.Path.Combine(Directory, "pinned.txt");

    /// <summary>Incidents the user wants to keep: <see cref="Prune"/> never removes them.</summary>
    public HashSet<string> Pinned()
    {
        try
        {
            return File.Exists(PinFile) ? File.ReadLines(PinFile).Select(l => l.Trim()).Where(IsValidId).ToHashSet(StringComparer.Ordinal) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void SetPinned(string id, bool pinned)
    {
        if (!IsValidId(id)) return;
        var all = Pinned();
        if (!(pinned ? all.Add(id) : all.Remove(id))) return;
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = PinFile + ".tmp";
        File.WriteAllLines(tmp, all.Order(StringComparer.Ordinal));
        File.Move(tmp, PinFile, overwrite: true);
    }

    /// <summary>
    /// Keeps at most <paramref name="keep"/> incidents. Automatic ones go first, oldest first: on a line that spikes all
    /// day they would otherwise push out the moments the user reported personally within days. Pinned incidents stay.
    /// </summary>
    public void Prune(int keep)
    {
        var ids = ListIds();
        var surplus = ids.Count - Math.Max(keep, 1);
        if (surplus <= 0) return;
        var pinned = Pinned();
        var byAge = ids.AsEnumerable().Reverse().Where(id => !pinned.Contains(id)).ToList();
        var order = byAge.Where(IsAutomatic).Concat(byAge.Where(id => !IsAutomatic(id)));
        foreach (var id in order.Take(surplus).ToList()) Delete(id);
    }

    private static bool IsAutomatic(string id) => id.EndsWith("-automatic", StringComparison.Ordinal);
}
