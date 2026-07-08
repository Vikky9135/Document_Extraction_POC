using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Endpoints;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Services;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ──────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

// ── OpenAPI (Scalar UI — available only in Development) ──────────────────────
builder.Services.AddOpenApi();

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<AgenticExtractionOptions>(
    builder.Configuration.GetSection(AgenticExtractionOptions.Section));

// ── Infrastructure ───────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

// ── Job Queue (Channel<T> — bounded, async) ──────────────────────────────────
var jobChannel = Channel.CreateBounded<ExtractionJob>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait
});
builder.Services.AddSingleton(jobChannel.Reader);
builder.Services.AddSingleton(jobChannel.Writer);

builder.Services.AddHttpClient("default").AddStandardResilienceHandler();

// ── Step 3 — Storage & Job Store ──────────────────────────────────────────────
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// ── Step 14 — Background worker (dequeues jobs, runs pipeline) ───────────────
builder.Services.AddHostedService<ExtractionJobProcessor>();

// TODO (Steps 5-13): uncomment as each component is implemented:
//   builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();
//   builder.Services.AddSingleton(new DocumentIntelligenceClient(
//       new Uri(opts.DocumentIntelligenceEndpoint),
//       new AzureKeyCredential(opts.DocumentIntelligenceKey)));
//   builder.Services.AddScoped<IOcrPreprocessingService, OcrPreprocessingService>();
//   builder.Services.AddScoped<ISchemaGenerationService, SchemaGenerationService>();
//   builder.Services.AddScoped<IExtractionAgent, ExtractionAgent>();
//   builder.Services.AddScoped<IVerificationAgent, VerificationAgent>();
//   builder.Services.AddScoped<IFormatterAgent, FormatterAgent>();
//   builder.Services.AddScoped<IGenerationAgent, GenerationAgent>();
//   builder.Services.AddScoped<IAgenticExtractionOrchestrator, AgenticExtractionOrchestrator>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                   // /openapi/v1.json
    app.MapScalarApiReference();        // /scalar/v1  ← open this in browser
}

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

app.MapExtractionEndpoints();

app.Run();

