using System.Text.Json;

namespace PageToMovie.Engine;

/// <summary>Shared JSON options (CA1869 — avoid allocating per serialize).</summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions IndentedCaseInsensitive = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
