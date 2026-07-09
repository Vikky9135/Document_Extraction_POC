using System.Text.Json;
using Azure.Storage.Blobs;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IBlobStorageService
{
    Task UploadPdfAsync(string jobId, Stream pdfStream, CancellationToken ct = default);
    Task<Stream> DownloadPdfAsync(string jobId, CancellationToken ct = default);
    Task SaveJsonAsync<T>(string jobId, string fileName, T obj, CancellationToken ct = default);
    Task<T?> LoadJsonAsync<T>(string jobId, string fileName, CancellationToken ct = default);
    Task<T?> ReadBlobContentAsync<T>(string blobPath, CancellationToken ct = default);
    Task<string?> ReadBlobAsJsonStringAsync(string blobPath, CancellationToken ct = default);
    string GetBlobPath(string jobId, string fileName) => $"jobs/{jobId}/{fileName}";
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public BlobStorageService(IOptions<AgenticExtractionOptions> opts)
    {
        var o = opts.Value;
        var serviceClient = new BlobServiceClient(o.BlobConnectionString);
        _container = serviceClient.GetBlobContainerClient(o.BlobContainerName);

        // Auto-create the container if it does not exist — no manual portal step needed.
        _container.CreateIfNotExists();
    }

    public async Task UploadPdfAsync(string jobId, Stream pdfStream, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"jobs/{jobId}/source.pdf");
        await blob.UploadAsync(pdfStream, overwrite: true, ct);
    }

    public async Task<Stream> DownloadPdfAsync(string jobId, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"jobs/{jobId}/source.pdf");
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task SaveJsonAsync<T>(string jobId, string fileName, T obj, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts);
        var blob = _container.GetBlobClient($"jobs/{jobId}/{fileName}");
        using var ms = new MemoryStream(bytes);
        await blob.UploadAsync(ms, overwrite: true, ct);
    }

    public async Task<T?> LoadJsonAsync<T>(string jobId, string fileName, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"jobs/{jobId}/{fileName}");
        if (!await blob.ExistsAsync(ct))
            return default;

        var response = await blob.DownloadContentAsync(ct);
        return JsonSerializer.Deserialize<T>(response.Value.Content.ToMemory().Span);
    }

    public async Task<T?> ReadBlobContentAsync<T>(string blobPath, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
            return default;

        var response = await blob.DownloadContentAsync(ct);
        return JsonSerializer.Deserialize<T>(response.Value.Content.ToMemory().Span);
    }

    public async Task<string?> ReadBlobAsJsonStringAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
            return null;

        var response = await blob.DownloadContentAsync(ct);
        // Convert BinaryData to UTF-8 string
        return System.Text.Encoding.UTF8.GetString(response.Value.Content.ToArray());
    }
}
