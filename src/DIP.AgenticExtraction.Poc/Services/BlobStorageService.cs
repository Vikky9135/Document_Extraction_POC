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
    string GetBlobPath(string jobId, string fileName) => $"jobs/{jobId}/{fileName}";
}

// TODO (Step 3): implement blob upload/download + JSON save/load.
public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    public BlobStorageService(IOptions<AgenticExtractionOptions> opts)
    {
        var o = opts.Value;
        var serviceClient = new BlobServiceClient(o.BlobConnectionString);
        _container = serviceClient.GetBlobContainerClient(o.BlobContainerName);
    }

    public Task UploadPdfAsync(string jobId, Stream pdfStream, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Stream> DownloadPdfAsync(string jobId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task SaveJsonAsync<T>(string jobId, string fileName, T obj, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<T?> LoadJsonAsync<T>(string jobId, string fileName, CancellationToken ct = default)
        => throw new NotImplementedException();
}
