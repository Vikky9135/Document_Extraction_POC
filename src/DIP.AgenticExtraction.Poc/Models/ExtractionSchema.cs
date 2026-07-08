using System.Text;

namespace DIP.AgenticExtraction.Poc.Models;

public enum FieldType { String, Number, Date, Integer, Time, Boolean }

public record GenericField
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public required string Description { get; init; }
    public List<string> Synonyms { get; init; } = [];
    public string? FieldFormat { get; init; }       // "YYYY-MM-DD", "2dp", "UPPERCASE"
    public bool UseVisionExtraction { get; init; }  // Use page images for this field

    // Returns description + synonyms string — used in JSON Schema "description" field.
    // On re-extraction pass, feedbackText is appended here.
    public string GetDescription(string? feedbackText = null)
    {
        var sb = new StringBuilder(Description);

        if (Synonyms.Count > 0)
        {
            sb.Append(" Synonyms: ").Append(string.Join(", ", Synonyms)).Append('.');
        }

        if (!string.IsNullOrWhiteSpace(feedbackText))
        {
            sb.Append(" CORRECTION NEEDED: ").Append(feedbackText);
        }

        return sb.ToString();
    }
}

public record SubField
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public string Description { get; init; } = "";
}

public record TableField
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public List<string> Synonyms { get; init; } = [];
    public required List<SubField> SubFields { get; init; }
}

public record GenerationField
{
    public required string Name { get; init; }
    public required string Instructions { get; init; }  // Plain-English compute instruction
    public required FieldType Type { get; init; }
    public string? FieldFormat { get; init; }
}

public record ExtractionSchema
{
    public required List<GenericField> Fields { get; init; }
    public required List<TableField> TableFields { get; init; }
    public List<GenerationField> GenerationFields { get; init; } = [];
    public ExtractionOptions Options { get; init; } = new();
}

public record ExtractionOptions
{
    public int MaxIter { get; init; } = 2;               // Correction loop iterations
    public bool IncludeImages { get; init; } = false;    // Vision mode per field
    public int ConfidenceThreshold { get; init; } = 70;  // Min confidence to accept
}
