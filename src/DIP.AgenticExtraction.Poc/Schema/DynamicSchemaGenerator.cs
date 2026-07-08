using System.Text.Json.Nodes;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Schema;

// The single most critical class. Converts GenericField[] into a strict JSON Schema
// at runtime, passed to Azure OpenAI with strict: true.
public static class DynamicSchemaGenerator
{
    // TODO (Step 7): build the JSON Schema for the ExtractionAgent structured output call.
    // feedbackOverrides injects verifier feedback into per-field descriptions (Phase 7).
    public static JsonObject GenerateExtractionSchema(
        IReadOnlyList<GenericField> fields,
        IReadOnlyList<TableField> tableFields,
        IReadOnlyDictionary<string, string>? feedbackOverrides = null)
        => throw new NotImplementedException();

    // TODO (Step 7): build the JSON Schema for the VerificationAgent call (Phase 6).
    public static JsonObject GenerateVerificationSchema(
        IReadOnlyDictionary<string, ExtractionFieldResult> extracted)
        => throw new NotImplementedException();
}
