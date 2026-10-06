using Microsoft.Extensions.AI;
using PaperDotNet.Abstractions;

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

    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> FailingInputs { get; } = new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _embedded = new(StringComparer.Ordinal);

    /// <summary>
    /// How often texts containing <paramref name="fragment"/> were embedded. Tests run in parallel against one
    /// generator, so a test counts only its own texts.
    /// </summary>
    public int Embedded(string fragment) =>
        _embedded.Where(e => e.Key.Contains(fragment, StringComparison.Ordinal)).Sum(e => e.Value);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            if (FailingInputs.Keys.Any(marker => text.Contains(marker, StringComparison.Ordinal))) { throw new InvalidOperationException("Test embedding provider is unavailable."); }
            _embedded.AddOrUpdate(text, 1, (_, count) => count + 1);
            var vector = new float[Dimensions];
            foreach (var word in FullTextQuery.Tokenize(text))
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
