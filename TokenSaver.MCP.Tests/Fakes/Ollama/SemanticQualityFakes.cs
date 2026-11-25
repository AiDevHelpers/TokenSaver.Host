#pragma warning disable CA1515 // Shared test fake referenced across test files; must stay public.
using System.Text.RegularExpressions;

namespace AiDevTools.TokenSaver.OllamaIntegration.Tests;

/// <summary>
///     Deterministic stand-ins for the summarize and embed models, for exercising the semantic
///     quality self test without a live model. <see cref="GoodSummary" /> answers like a model that
///     understands the fixture code; <see cref="BagOfWordsEmbed" /> places texts that share words near
///     each other, so a query lands on the summary that talks about the same thing.
/// </summary>
public static class SemanticQualityFakes
{
    private const int Dimensions = 128;

    public static string GoodSummary(string prompt)
    {
        if (prompt.Contains("Task.Delay", StringComparison.Ordinal))
        {
            return "Retries a failing network call with exponential backoff and gives up after five attempts.";
        }

        if (prompt.Contains("SHA256", StringComparison.Ordinal))
        {
            return "Computes the SHA-256 hash of a string and returns it as a hex digest.";
        }

        if (prompt.Contains("celsius", StringComparison.Ordinal))
        {
            return "Converts a temperature from celsius to fahrenheit.";
        }

        if (prompt.Contains("fields", StringComparison.Ordinal))
        {
            return "Splits a comma separated csv line into fields, respecting quoted commas.";
        }

        return "summary";
    }

    public static float[] BagOfWordsEmbed(string input)
    {
        float[] v = new float[Dimensions];
        foreach (Match m in Regex.Matches(input.ToUpperInvariant(), "[A-Z0-9]+"))
        {
            uint hash = 2166136261;
            foreach (char c in m.Value)
            {
                hash = (hash ^ c) * 16777619;
            }

            v[hash % Dimensions] += 1f;
        }

        return v;
    }
}
