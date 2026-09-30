using Microsoft.Extensions.AI;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// A deterministic stand-in for an embedding model: every word counts on one of <see cref="Dimensions"/> axes, and
/// synonyms share an axis (car, automobile, vehicle…), so texts about the same thing are similar without sharing words.
/// </summary>
internal sealed class ConceptEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 1024;

    public static readonly ConceptEmbeddingGenerator Instance = new();

    private static readonly Dictionary<string, string> Concepts = new(StringComparer.Ordinal)
    {
        ["automobile"] = "car",
        ["automobiles"] = "car",
        ["vehicle"] = "car",
        ["vehicles"] = "car",
        ["cars"] = "car",
        ["motorcar"] = "car",
        ["physician"] = "doctor",
        ["doctors"] = "doctor",
        ["medic"] = "doctor",
    };

    private int _embedded;

    /// <summary>Texts embedded so far (all calls).</summary>
    public int Embedded => Volatile.Read(ref _embedded);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            Interlocked.Increment(ref _embedded);
            var vector = new float[Dimensions];
            foreach (var word in PaperDotNet.Persistence.FullTextQuery.Tokenize(text))
            {
                vector[Axis(Concepts.GetValueOrDefault(word, word))] += 1;
            }

            result.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("test", null, "concepts", Dimensions)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>FNV-1a: stable across processes, unlike string.GetHashCode.</summary>
    private static int Axis(string word)
    {
        var hash = 2166136261;
        foreach (var c in word)
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)(hash % Dimensions);
    }
}
