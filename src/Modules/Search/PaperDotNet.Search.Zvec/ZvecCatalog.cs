using System.Text.Json;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// What a collection knows that zvec cannot list: the search fields it has seen (name, kind, key), which of them have a
/// number column, and how it was created (stemmed languages, prefix column, vector model). Kept as <c>{collection}.catalog.json</c> next to the
/// collection's folder, written by the collection's single writer.
/// </summary>
internal sealed class ZvecCatalog
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private State _state;

    private ZvecCatalog(string path, State state)
    {
        _path = path;
        _state = state;
    }

    private sealed record Field(string Name, SearchFieldKind Kind, string Key, bool Column);

    private sealed record State(List<Field> Fields, List<string> Languages, string? VectorModel)
    {
        public bool Prefix { get; init; }
    }

    public static ZvecCatalog Load(string collectionPath)
    {
        var path = collectionPath + ".catalog.json";
        var state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State([], [], null)
            : new State([], [], null);
        return new ZvecCatalog(path, state);
    }

    /// <summary>The embedding model key (hashed) the collection's vectors come from, or null for text only.</summary>
    public string? VectorModel => _state.VectorModel;

    /// <summary>Whether the collection has the trigram column for <c>word*</c> terms.</summary>
    public bool Prefix => _state.Prefix;

    /// <summary>The languages the collection has a stemmed column for.</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            lock (_gate)
            {
                return [.. _state.Languages];
            }
        }
    }

    public IReadOnlyList<(SearchFieldInfo Field, string Key)> Fields
    {
        get
        {
            lock (_gate)
            {
                return [.. _state.Fields.Select(f => (new SearchFieldInfo(f.Name, f.Kind), f.Key))];
            }
        }
    }

    public IReadOnlyList<string> NumberColumns
    {
        get
        {
            lock (_gate)
            {
                return [.. _state.Fields.Where(f => f.Column).Select(f => f.Key)];
            }
        }
    }

    public bool HasNumberColumn(string key)
    {
        lock (_gate)
        {
            return _state.Fields.Any(f => f.Key == key && f.Column);
        }
    }

    /// <summary>Records fields; <paramref name="addColumn"/> creates the number column of a new numeric field.</summary>
    public void Register(IEnumerable<SearchField> fields, Action<string> addColumn)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var field in fields.DistinctBy(f => (f.Name, f.Kind)))
            {
                var key = ZvecLayout.FieldKey(field.Name, field.Kind);
                if (_state.Fields.Any(f => f.Key == key))
                {
                    continue;
                }

                var numeric = !SearchField.IsText(field.Kind) && field.Kind != SearchFieldKind.Boolean;
                if (numeric)
                {
                    addColumn(ZvecLayout.NumberColumn(key));
                }

                _state.Fields.Add(new Field(field.Name, field.Kind, key, numeric));
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>Records how a new collection was created.</summary>
    public void Initialize(IReadOnlyList<string> languages, bool prefix, string? vectorModel)
    {
        lock (_gate)
        {
            _state = new State([], [.. languages], vectorModel) { Prefix = prefix };
            Save();
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, overwrite: true);
    }
}
