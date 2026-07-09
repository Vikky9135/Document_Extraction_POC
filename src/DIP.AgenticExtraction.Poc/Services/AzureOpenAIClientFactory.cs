using Azure;
using Azure.AI.OpenAI;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IAzureOpenAIClientFactory
{
    ChatClient CreateO3Client();      // Phases 5,6,7 — reasoning, high accuracy
    ChatClient CreateO4MiniClient();  // Phase 8 — fast + cheap formatting
    ChatClient CreateGpt5Client();    // Phases 4,9 — schema gen + code gen
}

public class AzureOpenAIClientFactory : IAzureOpenAIClientFactory
{
    private readonly AgenticExtractionOptions _opts;

    public AzureOpenAIClientFactory(IOptions<AgenticExtractionOptions> opts)
    {
        _opts = opts.Value;
        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_opts.AzureOpenAIEndpoint))
        {
            throw new InvalidOperationException("AzureOpenAIEndpoint is not configured. Check appsettings.Development.json");
        }

        if (string.IsNullOrWhiteSpace(_opts.AzureOpenAIKey))
        {
            throw new InvalidOperationException("AzureOpenAIKey is not configured. Check appsettings.Development.json");
        }

        if (string.IsNullOrWhiteSpace(_opts.Gpt5DeploymentName) ||
            string.IsNullOrWhiteSpace(_opts.O3DeploymentName) ||
            string.IsNullOrWhiteSpace(_opts.O4MiniDeploymentName))
        {
            throw new InvalidOperationException("Deployment names are not configured. Check appsettings.Development.json");
        }

        // Validate endpoint format
        if (!Uri.TryCreate(_opts.AzureOpenAIEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"Invalid AzureOpenAIEndpoint format: {_opts.AzureOpenAIEndpoint}");
        }
    }

    public ChatClient CreateO3Client()
        => CreateClient(_opts.O3DeploymentName);

    public ChatClient CreateO4MiniClient()
        => CreateClient(_opts.O4MiniDeploymentName);

    public ChatClient CreateGpt5Client()
        => CreateClient(_opts.Gpt5DeploymentName);

    private ChatClient CreateClient(string deploymentName)
    {
        try
        {
            var endpoint = new Uri(_opts.AzureOpenAIEndpoint);

            // TODO: switch to DefaultAzureCredential when using managed identity.
            var client = new AzureOpenAIClient(endpoint, new AzureKeyCredential(_opts.AzureOpenAIKey));

            return client.GetChatClient(deploymentName);
        }
        catch (Exception ex)
        {
            var message = $"Failed to create ChatClient for deployment '{deploymentName}'. " +
                         $"Endpoint: {_opts.AzureOpenAIEndpoint}, " +
                         $"Error: {ex.Message}";
            throw new InvalidOperationException(message, ex);
        }
    }
}
