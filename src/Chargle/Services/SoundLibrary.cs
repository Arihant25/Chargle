using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargle.Services;

public sealed class SoundPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string License { get; init; } = "";
    public bool IsBuiltIn { get; init; }

    public required string PlugPath { get; init; }
    public required string UnplugPath { get; init; }

    public CachedSound? Plug { get; private set; }
    public CachedSound? Unplug { get; private set; }

    /// <summary>True once both sounds are decoded and ready to fire with no further work.</summary>
    public bool IsLoaded => Plug is not null && Unplug is not null;

    public string? LoadError { get; private set; }

    public void Load()
    {
        if (IsLoaded) return;

        try
        {
            Plug = CachedSound.Load(PlugPath);
            Unplug = CachedSound.Load(UnplugPath);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Plug = Unplug = null;
            LoadError = ex.Message;
            Debug.WriteLine($"Chargle: could not load pack '{Id}'. {ex.Message}");
        }
    }
}

/// <summary>
/// Finds sound packs. A pack is just a folder with a <c>plug</c> and an <c>unplug</c> sound in it,
/// which means adding your own is a matter of dropping a folder in: no format to learn, no
/// registry, nothing to rebuild.
/// </summary>
public sealed class SoundLibrary
{
    /// <summary>Extensions we will try, in preference order.</summary>
    private static readonly string[] Extensions = [".wav", ".flac", ".mp3", ".m4a", ".ogg", ".wma", ".aiff"];

    private readonly List<SoundPack> _packs = [];

    public IReadOnlyList<SoundPack> Packs => _packs;

    /// <summary>Ships with the app, next to the executable.</summary>
    public static string BuiltInDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");

    /// <summary>Where a user drops their own packs. Opened by a button in Settings.</summary>
    public static string UserDirectory =>
        Path.Combine(AppPaths.DataDirectory, "Sounds");

    public void Reload()
    {
        _packs.Clear();

        Scan(BuiltInDirectory, builtIn: true);
        Scan(UserDirectory, builtIn: false);

        // Built-ins first, in the order they were designed to be heard (loudest and friendliest
        // to quietest), then anything the user has added, alphabetically.
        string[] order = ["chime", "red-fruit", "droplet", "glass", "swell", "pebble", "tick", "blip"];
        _packs.Sort((a, b) =>
        {
            if (a.IsBuiltIn != b.IsBuiltIn) return a.IsBuiltIn ? -1 : 1;
            if (!a.IsBuiltIn) return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return Array.IndexOf(order, a.Id).CompareTo(Array.IndexOf(order, b.Id));
        });
    }

    public SoundPack? Find(string? id) =>
        id is null ? null : _packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Decodes every pack up front. A few megabytes of RAM buys instant preview.</summary>
    public void LoadAll()
    {
        foreach (var pack in _packs) pack.Load();
    }

    /// <summary>The id of the pack that chosen files are copied into.</summary>
    public const string UserPackId = "my-sounds";

    /// <summary>
    /// Copies chosen audio into the user's own pack folder and returns the resulting pack.
    ///
    /// Copying rather than remembering a path is the whole point. Someone picks a file off their
    /// desktop, tidies the desktop a week later, and the app should not quietly stop working.
    /// Once it is here it belongs to Chargle.
    ///
    /// The pair is always completed from <paramref name="seed"/>, because half a pack is not a
    /// pack, and choosing a connect sound should not leave the disconnect one undefined.
    /// </summary>
    public SoundPack? ImportUserPack(SoundPack seed, string? plugSource, string? unplugSource)
    {
        string dir = Path.Combine(UserDirectory, UserPackId);
        Directory.CreateDirectory(dir);

        var existing = Find(UserPackId);
        Replace(dir, "plug", plugSource ?? existing?.PlugPath ?? seed.PlugPath);
        Replace(dir, "unplug", unplugSource ?? existing?.UnplugPath ?? seed.UnplugPath);

        var manifest = new PackManifest(
            UserPackId,
            "My sounds",
            "Your own files, copied here so they stay put.",
            Author: "",
            License: "",
            Generated: false);

        File.WriteAllText(
            Path.Combine(dir, "pack.json"),
            JsonSerializer.Serialize(manifest, ChargleJson.Default.PackManifest));

        Reload();
        return Find(UserPackId);
    }

    /// <summary>
    /// Puts one half of the pair in place. Copies to a temporary file before removing the old
    /// one, because the source is sometimes the file being replaced: completing the pair from
    /// the existing pack means reading out of the very folder being written to.
    /// </summary>
    private static void Replace(string dir, string stem, string source)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension.Length == 0) extension = ".wav";

        string staging = Path.Combine(dir, $"{stem}.importing{extension}");
        File.Copy(source, staging, overwrite: true);

        // Any extension may be present from a previous import, so clear them all rather than
        // guessing which one is there.
        foreach (string old in Directory.GetFiles(dir, stem + ".*"))
        {
            if (!old.Equals(staging, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }

        File.Move(staging, Path.Combine(dir, stem + extension), overwrite: true);
    }

    private void Scan(string root, bool builtIn)
    {
        if (!Directory.Exists(root)) return;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string? plug = FindSound(dir, "plug");
            string? unplug = FindSound(dir, "unplug");
            if (plug is null || unplug is null) continue;

            string id = Path.GetFileName(dir);
            var manifest = ReadManifest(dir);

            _packs.Add(new SoundPack
            {
                Id = manifest?.Id ?? id,
                Name = manifest?.Name ?? Prettify(id),
                Description = manifest?.Description ?? "",
                Author = manifest?.Author ?? "",
                License = manifest?.License ?? "",
                IsBuiltIn = builtIn,
                PlugPath = plug,
                UnplugPath = unplug,
            });
        }
    }

    private static string? FindSound(string dir, string stem)
    {
        foreach (string ext in Extensions)
        {
            string candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static PackManifest? ReadManifest(string dir)
    {
        string path = Path.Combine(dir, "pack.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ChargleJson.Default.PackManifest);
        }
        catch (Exception ex)
        {
            // A malformed pack.json should downgrade to "folder name as title", not break startup.
            Debug.WriteLine($"Chargle: ignoring bad pack.json in {dir}. {ex.Message}");
            return null;
        }
    }

    /// <summary>"my-sounds" -> "My sounds", for folders dropped in without a manifest.</summary>
    private static string Prettify(string id)
    {
        string spaced = id.Replace('-', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0 ? id : char.ToUpper(spaced[0]) + spaced[1..];
    }
}

public sealed record PackManifest(
    string? Id,
    string? Name,
    string? Description,
    string? Author,
    string? License,
    bool Generated = false);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(ChargleSettings))]
internal sealed partial class ChargleJson : JsonSerializerContext;
