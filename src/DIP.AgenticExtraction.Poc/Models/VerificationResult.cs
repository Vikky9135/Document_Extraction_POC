namespace DIP.AgenticExtraction.Poc.Models;

public record VerificationFieldResult
{
    public bool Correct { get; init; }
    public string Feedback { get; init; } = "";
}

public record VerificationResult
{
    // Key = field name, Value = verdict
    public Dictionary<string, VerificationFieldResult> Fields { get; init; } = [];

    public IEnumerable<string> FailedFieldNames =>
        Fields.Where(kvp => !kvp.Value.Correct).Select(kvp => kvp.Key);

    public bool AllCorrect => Fields.Values.All(v => v.Correct);
}
