using Azure;
using Azure.AI.OpenAI;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IAzureOpenAIClientFactory
{
    ChatClient CreateGpt54Client();      // Schema gen, extraction, verification, code gen
    ChatClient CreateGpt54MiniClient();  // Correction, merge, dedup, formatting
}

public class AzureOpenAIClientFactory : IAzureOpenAIClientFactory
{
    private readonly AgenticExtractionOptions _opts;

    public AzureOpenAIClientFactory(IOptions<AgenticExtractionOptions> opts)
        => _opts = opts.Value;

    public ChatClient CreateGpt54Client()
        => CreateClient(_opts.Gpt54DeploymentName);

    public ChatClient CreateGpt54MiniClient()
        => CreateClient(_opts.Gpt54MiniDeploymentName);

    private ChatClient CreateClient(string deploymentName)
    {
        var endpoint = new Uri(_opts.AzureOpenAIEndpoint);

        var options = new AzureOpenAIClientOptions(
            AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        var client = new AzureOpenAIClient(
            endpoint, new AzureKeyCredential(_opts.AzureOpenAIKey), options);

        return client.GetChatClient(deploymentName);
    }
}
