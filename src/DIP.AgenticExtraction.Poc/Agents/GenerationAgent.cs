using System.Text.Json;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public record GenerationResult(
    Dictionary<string, object?> Results,
    int LlmCallCount,
    Dictionary<string, string> GeneratedScripts);

// ScriptGlobals: the data context available inside Roslyn scripts.
public class ScriptGlobals
{
    public Dictionary<string, ExtractionFieldResult> Data { get; set; } = [];
    public DateTime Today { get; set; } = DateTime.UtcNow.Date;
}

public interface IGenerationAgent
{
    // Phase 9 — GPT-5 writes a C# script per computed field; Roslyn executes it.
    Task<GenerationResult> ComputeAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default);
}

// Phase 9 — uses GPT-5 for C# script gen + Roslyn (CSharpScript) for deterministic execution.
public class GenerationAgent : IGenerationAgent
{
    private readonly ChatClient _gpt5Client;

    // Unsafe tokens — any generated code containing these is rejected before execution.
    private static readonly string[] BlockedPatterns =
    [
        "File.", "Directory.", "Process.", "HttpClient", "Assembly.", "Environment.",
        "Console.", "Thread.", "Task.", "Type.GetType", "Activator.", "Marshal.",
        "unsafe", "DllImport", "extern", "System.IO", "System.Net", "System.Reflection",
        "System.Diagnostics", "AppDomain", "GC."
    ];

    public GenerationAgent(ChatClient gpt5Client) => _gpt5Client = gpt5Client;

    public async Task<GenerationResult> ComputeAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, object?>();
        var scripts = new Dictionary<string, string>();
        int llmCalls = 0;

        if (schema.GenerationFields.Count == 0)
            return new GenerationResult(results, 0, scripts);

        // A mutable working set — each computed field becomes available to later fields.
        var working = new Dictionary<string, ExtractionFieldResult>(fields);

        var scriptOptions = ScriptOptions.Default
            .AddImports("System", "System.Linq", "System.Collections.Generic")
            .AddReferences(
                typeof(Enumerable).Assembly,
                typeof(ExtractionFieldResult).Assembly);

        // Process sequentially — a later field may depend on an earlier computed value.
        foreach (var field in schema.GenerationFields)
        {
            // 1. Ask GPT-5 to write a C# script for the computed field.
            var dataJson = JsonSerializer.Serialize(working.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Value?.ToString() ?? ""));

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompts.GenerationSystemPrompt),
                new UserChatMessage(
                    $"TASK: {field.Instructions}\n\n" +
                    $"RESULT TYPE: {field.Type}\n\n" +
                    $"AVAILABLE DATA (Dictionary<string, ExtractionFieldResult> Data — access via Data[\"name\"].Value):\n" +
                    $"{dataJson}\n\n" +
                    "Write a C# script that computes the result. " +
                    "Use Data[\"fieldName\"].Value to access extracted values and cast as needed " +
                    "(e.g. double.Parse(Data[\"totalAmount\"].Value?.ToString() ?? \"0\")). " +
                    "Use the Today variable for the current date. " +
                    "IMPORTANT: Declare a result variable, assign to it in branches, and put it alone on the last line. " +
                    "Return ONLY the script — no method wrapper, no class.")
            };

            llmCalls++;
            var response = await _gpt5Client.CompleteChatAsync(messages, cancellationToken: ct);
            var code = CleanCode(response.Value.Content[0].Text);

            // 2. Safety check — reject anything touching IO, reflection, processes, etc.
            if (string.IsNullOrWhiteSpace(code)
                || BlockedPatterns.Any(p => code.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                results[field.Name] = null;
                scripts[field.Name] = $"// REJECTED (blocked pattern detected): {code}";
                continue;
            }

            // Store the generated script
            scripts[field.Name] = code;

            // 3. Execute with Roslyn in a sandboxed globals context.
            try
            {
                var globals = new ScriptGlobals
                {
                    Data  = working,
                    Today = DateTime.UtcNow.Date
                };

                var value = await CSharpScript.EvaluateAsync<object?>(
                    code, scriptOptions, globals, typeof(ScriptGlobals), ct);

                results[field.Name] = value;

                // Make the computed value available to subsequent generation fields.
                working[field.Name] = new ExtractionFieldResult
                {
                    Value      = value,
                    Confidence = 100,
                    IsVerified = true,
                    RawStr     = value?.ToString() ?? ""
                };
            }
            catch (CompilationErrorException)
            {
                results[field.Name] = null;
            }
            catch (Exception)
            {
                results[field.Name] = null;
            }
        }

        return new GenerationResult(results, llmCalls, scripts);
    }

    // Strips markdown fences / stray backticks the model may add.
    private static string CleanCode(string raw)
    {
        var code = raw.Trim();

        if (code.StartsWith("```"))
        {
            var firstNewline = code.IndexOf('\n');
            if (firstNewline >= 0) code = code[(firstNewline + 1)..];
            if (code.EndsWith("```")) code = code[..^3];
        }

        return code.Trim();
    }
}
