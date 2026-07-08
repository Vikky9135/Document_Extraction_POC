namespace DIP.AgenticExtraction.Poc.Prompts;

// Step 15 — all LLM system prompts live here.
public static class SystemPrompts
{
    // TODO (Step 15): fill in the real prompt text.

    public const string ExtractorSystemPrompt = """
        You are a precise document data extractor.
        TODO: full extractor prompt.
        """;

    public const string VerifierSystemPrompt = """
        You are a fact-checker. Be skeptical and critical.
        TODO: full verifier prompt.
        """;

    public const string FormatterSystemPrompt = """
        You convert raw values into the requested output format.
        TODO: full formatter prompt.
        """;

    public const string SchemaGenSystemPrompt = """
        You analyze a document and design an extraction schema.
        TODO: full schema-generation prompt.
        """;

    public const string GenerationSystemPrompt = """
        You write a single C# expression to compute a derived field.
        TODO: full generation prompt.
        """;
}
