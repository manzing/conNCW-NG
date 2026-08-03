using System.Text.Json;

namespace ConNCW.Core.Signatures;

/// <summary>
/// Gère la liste extensible de signatures NCW connues (signatures.json).
/// Remplace la comparaison octet-par-octet figée du parser Delphi original
/// par une comparaison en bloc contre une liste chargée dynamiquement.
/// </summary>
public sealed class SignatureStore
{
    private readonly List<SignatureEntry> _entries = new();
    private readonly string _path;

    public IReadOnlyList<SignatureEntry> Entries => _entries;

    public SignatureStore(string path)
    {
        _path = path;
        Load();
    }

    public void Load()
    {
        _entries.Clear();
        if (!File.Exists(_path))
        {
            return;
        }

        var json = File.ReadAllText(_path);
        var loaded = JsonSerializer.Deserialize<List<SignatureEntry>>(json);
        if (loaded is not null)
        {
            _entries.AddRange(loaded);
        }
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(_entries, options);
        File.WriteAllText(_path, json);
    }

    /// <summary>
    /// Compare la signature lue en bloc entier contre chaque entrée connue
    /// (au lieu d'une comparaison octet par octet comme dans le code Delphi d'origine).
    /// </summary>
    public SignatureEntry? Match(byte[] candidate)
    {
        foreach (var entry in _entries)
        {
            var known = entry.ToBytes();
            if (known.Length <= candidate.Length &&
                candidate.AsSpan(0, known.Length).SequenceEqual(known))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Ajoute une signature apprise après validation manuelle (--learn-signature).
    /// </summary>
    public void AddLearned(byte[] signatureBytes, string label, string? notes = null)
    {
        var hex = Convert.ToHexString(signatureBytes);
        if (_entries.Any(e => e.HexBytes.Equals(hex, StringComparison.OrdinalIgnoreCase)))
        {
            return; // déjà présente
        }

        _entries.Add(new SignatureEntry
        {
            Label = label,
            HexBytes = hex,
            Source = "learned",
            Notes = notes
        });
        Save();
    }
}
