using Azure;
using Azure.AI.OpenAI;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IAzureOpenAIClientFactory
{
    ChatClient CreateO3Client();      // Phases 5,6,7 — reasoning, high accuracy
    ChatClient CreateGpt5MiniClient(); // Phase 8 — fast + cheap formatting
    ChatClient CreateGpt5Client();    // Phases 4,9 — schema gen + code gen
}

public class AzureOpenAIClientFactory : IAzureOpenAIClientFactory
{
    private readonly AgenticExtractionOptions _opts;

    public AzureOpenAIClientFactory(IOptions<AgenticExtractionOptions> opts)
        => _opts = opts.Value;

    public ChatClient CreateO3Client()
        => CreateClient(_opts.O3DeploymentName);

    public ChatClient CreateGpt5MiniClient()
        => CreateClient(_opts.Gpt5MiniDeploymentName);

    public ChatClient CreateGpt5Client()
        => CreateClient(_opts.Gpt5DeploymentName);

    private ChatClient CreateClient(string deploymentName)
    {
        var endpoint = new Uri(_opts.AzureOpenAIEndpoint);

        // o3 / gpt-5-mini models require API version 2024-12-01-preview or later.
        var options = new AzureOpenAIClientOptions(
            AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        // TODO: switch to DefaultAzureCredential when using managed identity.
        var client = new AzureOpenAIClient(
            endpoint, new AzureKeyCredential(_opts.AzureOpenAIKey), options);

        return client.GetChatClient(deploymentName);
    }
}
