namespace DIP.AgenticExtraction.Poc.Options;

public class AgenticExtractionOptions
{
    public const string Section = "AgenticExtraction";

    public string O3DeploymentName { get; set; } = "o3";              // Phases 5,6,7 — reasoning model
    public string Gpt5MiniDeploymentName { get; set; } = "gpt-5-mini"; // Phase 8 — fast + cheap formatting
    public string Gpt5DeploymentName { get; set; } = "gpt-5";         // Phases 4,9 — schema gen + code gen
    public string AzureOpenAIEndpoint { get; set; } = "";
    public string AzureOpenAIKey { get; set; } = "";              // Prefer managed identity in production
    public string DocumentIntelligenceEndpoint { get; set; } = "";
    public string DocumentIntelligenceKey { get; set; } = "";
    public string BlobConnectionString { get; set; } = "";
    public string BlobContainerName { get; set; } = "agentic-poc-jobs";
    public int MaxFileSizeMb { get; set; } = 50;
    public int MaxOcrPagesPerChunk { get; set; } = 100;

    // o3 reasoning effort: "low" | "medium" | "high"
    // Extraction + Verification = high, Correction = medium
    public string O3ReasoningEffort { get; set; } = "high";
}
