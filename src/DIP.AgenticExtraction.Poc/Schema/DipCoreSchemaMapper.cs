using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Schema;

// DIP Core integration only — maps ExtractionField SQL entities to GenericField[].
// This is NOT used in the standalone POC flow; it is used by the future
// AgenticPromptExtractionAdapter when integrating into DIP Core.
public static class DipCoreSchemaMapper
{
    // TODO (Step 6): map DIP Core SQL ExtractionFields -> GenericField[].
    public static List<GenericField> Map(IEnumerable<DipCoreExtractionFieldDto> fields)
        => throw new NotImplementedException();
}

public record DipCoreExtractionFieldDto
{
    public required string FieldTitle { get; init; }
    public string? Description { get; init; }
    public string? DataType { get; init; }
    public string? PromptText { get; init; }
    public string? FieldFormat { get; init; }
    public string? TableGroupName { get; init; }
}
