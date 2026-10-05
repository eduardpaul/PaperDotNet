using Microsoft.Extensions.Configuration;

namespace PaperDotNet.Ocr;

/// <summary>Detection and recognition share one inference budget across all service providers and tenants.</summary>
internal static class PaddleInferenceBudget
{
    internal static SemaphoreSlim Gate { get; } = new(1, 1);

    internal static int MaxDimension(IConfiguration configuration)
    {
        var dimension = configuration.GetValue<int?>("Ocr:PaddleMaxDetectionDimension") ?? 1536;
        if (dimension is < 256 or > 2600 || dimension % 32 != 0)
            throw new InvalidOperationException("Ocr:PaddleMaxDetectionDimension must be a multiple of 32 between 256 and 2600.");
        return dimension;
    }

    // Limit feature-map area as well as the longest side. A square phone photo needs
    // a lower dimension than a receipt to stay within the same native working-memory budget.
    internal static int DetectionDimension(int width, int height, int maximum) =>
        Math.Min(maximum, Math.Max(32, (int)Math.Sqrt(1_048_576.0 * Math.Max(width, height) / Math.Min(width, height)) / 32 * 32));

}
