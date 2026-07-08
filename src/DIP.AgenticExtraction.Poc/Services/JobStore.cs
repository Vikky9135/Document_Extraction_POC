using DIP.AgenticExtraction.Poc.Models;
using Microsoft.Extensions.Caching.Memory;

namespace DIP.AgenticExtraction.Poc.Services;

public interface IJobStore
{
    void Set(string jobId, JobStatus status, string? error = null);
    (JobStatus Status, string? Error) Get(string jobId);
}

// TODO (Step 4): back with IMemoryCache. Swap to Redis/SQL for production.
public class JobStore : IJobStore
{
    private readonly IMemoryCache _cache;

    public JobStore(IMemoryCache cache) => _cache = cache;

    public void Set(string jobId, JobStatus status, string? error = null)
        => _cache.Set(JobKey(jobId), (status, error));

    public (JobStatus Status, string? Error) Get(string jobId)
        => _cache.TryGetValue(JobKey(jobId), out (JobStatus, string?) v)
            ? v
            : (JobStatus.Queued, null);

    private static string JobKey(string jobId) => $"job:{jobId}";
}
