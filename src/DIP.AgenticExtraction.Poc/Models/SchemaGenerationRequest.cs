namespace DIP.AgenticExtraction.Poc.Models;

public class SchemaGenerationRequest
{
    public required string UserPrompt { get; set; }
    public required string DocumentId { get; set; }
}
