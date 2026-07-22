namespace DIP.AgenticExtraction.Poc.Options;

public class AgenticExtractionOptions
{
    public const string Section = "AgenticExtraction";

    // Model deployments
    public string Gpt54DeploymentName { get; set; } = "gpt-5.4";       // Schema gen, extraction, verification, code gen
    public string Gpt54MiniDeploymentName { get; set; } = "gpt-5.4-mini"; // Correction, merge, dedup, formatting
    public string AzureOpenAIEndpoint { get; set; } = "";
    public string AzureOpenAIKey { get; set; } = "";

    // Document Intelligence (OCR)
    public string DocumentIntelligenceEndpoint { get; set; } = "";
    public string DocumentIntelligenceKey { get; set; } = "";

    // Blob storage
    public string BlobConnectionString { get; set; } = "";
    public string BlobContainerName { get; set; } = "agentic-poc-jobs";
    public int MaxFileSizeMb { get; set; } = 50;

    // Chunking — page-based splitting for large documents
    // gpt-5.4 has 922K input but the cheap pricing tier is ≤272K.
    // At ~1,000 tokens/page, 200 pages ≈ 200K tokens (under the 272K boundary).
    public int SchemaChunkPages { get; set; } = 200;      // Pages per chunk for schema generation
    public int ExtractionChunkPages { get; set; } = 200;   // Pages per chunk for extraction
    public int ExtractionOverlapPages { get; set; } = 2;   // Overlap between extraction chunks
}
