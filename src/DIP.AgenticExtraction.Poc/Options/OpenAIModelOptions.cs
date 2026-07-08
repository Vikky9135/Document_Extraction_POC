namespace DIP.AgenticExtraction.Poc.Options;

// Per-model configuration. Reserved for future use — e.g. per-model temperature,
// max tokens, or reasoning effort overrides. Kept as a skeleton for now.
public class OpenAIModelOptions
{
    public string DeploymentName { get; set; } = "";
    public float? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
    public string? ReasoningEffort { get; set; }
}
