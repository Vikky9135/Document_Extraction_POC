using System.Text;

namespace DIP.AgenticExtraction.Poc.Models;

public enum FieldType { String, Number, Date, Integer, Time, Boolean }

/// <summary>
/// Role of a field in the extraction schema.
/// "Extract" = directly requested by the user.
/// "Source" = present in the document, included only because it is needed to compute a generationField.
/// </summary>
public enum FieldRole { Extract, Source }

public record GenericField
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public required string Description { get; init; }
    public List<string> Synonyms { get; init; } = [];
    public string? FieldFormat { get; init; }       // "YYYY-MM-DD", "2dp", "UPPERCASE"
    public bool UseVisionExtraction { get; init; }  // Use page images for this field
    public FieldRole Role { get; init; } = FieldRole.Extract;  // "extract" or "source"

    /// <summary>
    /// Accumulated correction hints from past extraction runs.
    /// Persisted into final-schema.json so future runs benefit from past corrections.
    /// </summary>
    public List<string> Hints { get; init; } = [];

    // Returns description + synonyms + hints string — used in JSON Schema "description" field.
    // On re-extraction pass, feedbackText is appended here.
    public string GetDescription(string? feedbackText = null)
    {
        var sb = new StringBuilder(Description);

        if (Synonyms.Count > 0)
        {
            sb.Append(" Synonyms: ").Append(string.Join(", ", Synonyms)).Append('.');
        }

        if (Hints.Count > 0)
        {
            sb.Append(" HINTS FROM PAST CORRECTIONS: ").Append(string.Join("; ", Hints)).Append('.');
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

// ── Validation schema — mirrors DocuFlow SchemaValidationAgent ───────────────
// Generated at design-time from the extraction schema + user prompt.
// Applied after extraction: each rule is checked against the extracted value.
public enum ValidationSeverity { Warning, Error }

public record ValidationRule
{
    // camelCase field name — must match a key in ExtractionSchema.Fields
    public required string FieldName { get; init; }

    // Natural-language condition, e.g.:
    //   "must be greater than 0"
    //   "must not be empty"
    //   "must be a valid date in the past"
    //   "must match format YYYY-MM-DD"
    public required string Condition { get; init; }

    // Message surfaced in ExtractionJobResult when the rule fails
    public required string ErrorMessage { get; init; }

    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;
}

// ── Three schema artifacts — mirrors DocuFlow's three schema agents ───────────
public record ExtractionSchema
{
    // Artifact 1 — extraction schema (SchemaAgent equivalent)
    // Fields and tables to pull directly from the document
    public required List<GenericField> Fields { get; init; }
    public required List<TableField> TableFields { get; init; }

    // Artifact 2 — generation schema (SchemaGeneratorAgent equivalent)
    // Derived/computed fields that are calculated from extracted values, not extracted
    public List<GenerationField> GenerationFields { get; init; } = [];

    // Artifact 3 — validation schema (SchemaValidationAgent equivalent)
    // Business rules applied to extracted values after extraction completes
    public List<ValidationRule> ValidationRules { get; init; } = [];

    public ExtractionOptions Options { get; init; } = new();
}

public record ExtractionOptions
{
    public int MaxIter { get; init; } = 2;               // Correction loop iterations
    public bool IncludeImages { get; init; } = false;    // Vision mode per field
    public int ConfidenceThreshold { get; init; } = 70;  // Min confidence to accept
}
