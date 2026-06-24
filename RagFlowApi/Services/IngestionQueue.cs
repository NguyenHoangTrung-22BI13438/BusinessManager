using System.Collections.Concurrent;
using System.Threading.Channels;

namespace RagFlowApi.Services;

// ── Job ───────────────────────────────────────────────────────────────────────
public record IngestionJob(
    string JobId,
    string DatasetId,
    byte[] Bytes,
    string FileName,
    string ContentType
);

// ── Status ────────────────────────────────────────────────────────────────────
public enum JobState { Pending, Processing, Done, Failed }

public class JobStatus
{
    public required string  JobId      { get; init; }
    public required string  FileName   { get; init; }
    public JobState          State      { get; set; } = JobState.Pending;
    public string?           Message    { get; set; }
    public DateTime          QueuedAt   { get; init; } = DateTime.UtcNow;
    public DateTime?         FinishedAt { get; set; }
}

// ── Channel singleton — one bounded in-process queue ─────────────────────────
public class IngestionChannel
{
    private readonly Channel<IngestionJob> _ch =
        Channel.CreateBounded<IngestionJob>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelWriter<IngestionJob> Writer => _ch.Writer;
    public ChannelReader<IngestionJob> Reader => _ch.Reader;
}

// ── Job store singleton — tracks state for each job ───────────────────────────
public class IngestionJobStore
{
    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new();

    public void Add(JobStatus status)    => _jobs[status.JobId] = status;

    public JobStatus? Get(string jobId)  =>
        _jobs.TryGetValue(jobId, out var s) ? s : null;

    public void Update(string jobId, Action<JobStatus> mutate)
    {
        if (_jobs.TryGetValue(jobId, out var s)) mutate(s);
    }

    // Remove entries that finished more than 1 h ago to prevent unbounded growth
    public void Purge()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var kv in _jobs.ToArray())
            if (kv.Value.FinishedAt.HasValue && kv.Value.FinishedAt < cutoff)
                _jobs.TryRemove(kv.Key, out _);
    }
}

// ── Background worker — drains the channel one job at a time ─────────────────
public sealed class IngestionWorker : BackgroundService
{
    private readonly IngestionChannel        _channel;
    private readonly IngestionJobStore       _store;
    private readonly IServiceScopeFactory    _scopes;
    private readonly ILogger<IngestionWorker> _log;
    private readonly string                  _vllmBase;

    public IngestionWorker(
        IngestionChannel channel, IngestionJobStore store,
        IServiceScopeFactory scopes, ILogger<IngestionWorker> log,
        IConfiguration config)
    {
        _channel  = channel; _store = store; _scopes = scopes; _log = log;
        _vllmBase = config["DotsOcr:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    // Returns true when VLLM answers /v1/models within 5 s; false otherwise.
    private async Task<bool> VllmIsReachableAsync()
    {
        if (string.IsNullOrWhiteSpace(_vllmBase)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.GetAsync($"{_vllmBase}/v1/models");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // True when a job will hit the VLLM OCR path.
    private static bool NeedsVllm(IngestionJob job)
    {
        var ext = Path.GetExtension(job.FileName).ToLowerInvariant();
        return ext == ".pdf" || job.ContentType.StartsWith("image/");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // ReadAllAsync blocks here until a job arrives or the app shuts down
        await foreach (var job in _channel.Reader.ReadAllAsync(ct))
        {
            _store.Update(job.JobId, s => s.State = JobState.Processing);
            _log.LogInformation("[Ingest] Start {Id} — {File}", job.JobId, job.FileName);

            try
            {
                // ── Pre-flight: fail fast if VLLM is down ─────────────────────
                if (NeedsVllm(job) && !await VllmIsReachableAsync())
                {
                    _log.LogWarning("[Ingest] VLLM unreachable — failing job {Id}", job.JobId);
                    _store.Update(job.JobId, s =>
                    {
                        s.State      = JobState.Failed;
                        s.Message    = "OCR service (VLLM) is unreachable. Please try again later.";
                        s.FinishedAt = DateTime.UtcNow;
                    });
                    continue;
                }

                // IngestionPipeline is Scoped — create a fresh scope per job
                await using var scope = _scopes.CreateAsyncScope();
                var pipeline = scope.ServiceProvider
                                    .GetRequiredService<IngestionPipeline>();

                await pipeline.IngestAsync(
                    job.DatasetId,
                    new ByteArrayFormFile(job.Bytes, job.FileName, job.ContentType));

                _store.Update(job.JobId, s =>
                {
                    s.State      = JobState.Done;
                    s.FinishedAt = DateTime.UtcNow;
                });
                _log.LogInformation("[Ingest] Done  {Id}", job.JobId);
            }
            catch (Exception ex)
            {
                _store.Update(job.JobId, s =>
                {
                    s.State      = JobState.Failed;
                    s.Message    = ex.Message;
                    s.FinishedAt = DateTime.UtcNow;
                });
                _log.LogError(ex, "[Ingest] Fail  {Id}", job.JobId);
            }

            _store.Purge();
        }
    }
}

// ── Reparse job ───────────────────────────────────────────────────────────────
// Separate from IngestionJob/IngestionChannel because reparse re-reads a cached
// file and deletes+recreates the document, rather than ingesting a fresh upload.
// Kept on its own single-consumer channel for the same reason uploads are:
// the OCR backend has limited GPU headroom and must process one document at a
// time, not N reparses in parallel (that's what caused the earlier OOM crash).
public record ReparseJob(
    string JobId,
    string DatasetId,
    string DocumentId,
    string FileName
);

public class ReparseChannel
{
    private readonly Channel<ReparseJob> _ch =
        Channel.CreateBounded<ReparseJob>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelWriter<ReparseJob> Writer => _ch.Writer;
    public ChannelReader<ReparseJob> Reader => _ch.Reader;
}

public sealed class ReparseWorker : BackgroundService
{
    private readonly ReparseChannel        _channel;
    private readonly IngestionJobStore     _store;
    private readonly IServiceScopeFactory  _scopes;
    private readonly ILogger<ReparseWorker> _log;

    public ReparseWorker(
        ReparseChannel channel, IngestionJobStore store,
        IServiceScopeFactory scopes, ILogger<ReparseWorker> log)
    {
        _channel = channel; _store = store; _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(ct))
        {
            _store.Update(job.JobId, s => s.State = JobState.Processing);
            _log.LogInformation("[Reparse] Start {Id} — {File}", job.JobId, job.FileName);

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IngestionPipeline>();

                await pipeline.ReingestAsync(job.DatasetId, job.DocumentId, job.FileName);

                _store.Update(job.JobId, s =>
                {
                    s.State      = JobState.Done;
                    s.FinishedAt = DateTime.UtcNow;
                });
                _log.LogInformation("[Reparse] Done  {Id}", job.JobId);
            }
            catch (Exception ex)
            {
                _store.Update(job.JobId, s =>
                {
                    s.State      = JobState.Failed;
                    s.Message    = ex.Message;
                    s.FinishedAt = DateTime.UtcNow;
                });
                _log.LogError(ex, "[Reparse] Fail  {Id}", job.JobId);
            }

            _store.Purge();
        }
    }
}

// ── IFormFile adapter — wraps a byte array so IngestionPipeline stays unchanged ──
internal sealed class ByteArrayFormFile : IFormFile
{
    private readonly byte[] _bytes;

    public ByteArrayFormFile(byte[] bytes, string fileName, string contentType)
    {
        _bytes = bytes; FileName = fileName; ContentType = contentType;
    }

    public string           ContentType        { get; }
    public string           ContentDisposition =>
        $"form-data; name=\"file\"; filename=\"{FileName}\"";
    public IHeaderDictionary Headers           => new HeaderDictionary();
    public long             Length             => _bytes.Length;
    public string           Name               => "file";
    public string           FileName           { get; }

    public void  CopyTo(Stream target)                          => target.Write(_bytes);
    public async Task CopyToAsync(Stream target, CancellationToken ct = default)
        => await target.WriteAsync(_bytes, ct);
    public Stream OpenReadStream() => new MemoryStream(_bytes);
}
