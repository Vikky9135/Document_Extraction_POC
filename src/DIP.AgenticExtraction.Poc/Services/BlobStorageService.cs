using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IBlobStorageService
{
    /// <summary>Upload a PDF to classification folder: {classificationId}/{fileName}.pdf</summary>
    Task UploadPdfAsync(string classificationId, string fileName, Stream pdfStream, CancellationToken ct = default);

    /// <summary>Save JSON to classification folder: {classificationId}/{fileName}</summary>
    Task SaveJsonAsync<T>(string classificationId, string fileName, T obj, CancellationToken ct = default);

    /// <summary>Load JSON from classification folder: {classificationId}/{fileName}</summary>
    Task<T?> LoadJsonAsync<T>(string classificationId, string fileName, CancellationToken ct = default);

    /// <summary>List all blobs under a classification prefix with an optional suffix filter.</summary>
    Task<List<string>> ListBlobsAsync(string classificationId, string? suffixFilter = null, CancellationToken ct = default);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public BlobStorageService(IOptions<AgenticExtractionOptions> opts)
    {
        var o = opts.Value;
        var serviceClient = new BlobServiceClient(o.BlobConnectionString);
        _container = serviceClient.GetBlobContainerClient(o.BlobContainerName);

        // Auto-create the container if it does not exist — no manual portal step needed.
        _container.CreateIfNotExists();
    }

    public async Task UploadPdfAsync(string classificationId, string fileName, Stream pdfStream, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient($"{classificationId}/{fileName}");
        await blob.UploadAsync(pdfStream, overwrite: true, cancellationToken: ct);
    }

    public async Task SaveJsonAsync<T>(string classificationId, string fileName, T obj, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient($"{classificationId}/{fileName}");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOptions);
        using var ms = new MemoryStream(bytes);
        await blob.UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    public async Task<T?> LoadJsonAsync<T>(string classificationId, string fileName, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"{classificationId}/{fileName}");
        if (!await blob.ExistsAsync(ct)) return default;
        var response = await blob.DownloadContentAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<T>(response.Value.Content.ToArray(), JsonOptions);
    }

    public async Task<List<string>> ListBlobsAsync(string classificationId, string? suffixFilter = null, CancellationToken ct = default)
    {
        var results = new List<string>();
        var prefix = $"{classificationId}/";
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
        {
            if (suffixFilter is null || item.Name.EndsWith(suffixFilter, StringComparison.OrdinalIgnoreCase))
                results.Add(item.Name);
        }
        return results;
    }
}
