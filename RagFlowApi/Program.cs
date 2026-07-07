using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using RagFlowApi.Models;
using RagFlowApi.Services;
using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<DotsOcrClient>();
builder.Services.AddHttpClient<PaddleOcrClient>();
builder.Services.AddScoped<IParser, DotsOcrParser>();
builder.Services.AddSingleton<LayoutChunker>();
builder.Services.AddScoped<IngestionPipeline>();
builder.Services.AddScoped<RagasService>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UserContext>();
builder.Services.AddSingleton<PendingDocumentStore>();
builder.Services.AddSingleton<RatingStore>();
builder.Services.AddSingleton<FormTemplateCache>();
builder.Services.AddSingleton<FormLibraryStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddScoped<DocxFormFillerService>();

// ── Async ingestion queue ─────────────────────────────────────────────────────
builder.Services.AddSingleton<IngestionChannel>();
builder.Services.AddSingleton<IngestionJobStore>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddSingleton<ReparseChannel>();
builder.Services.AddHostedService<ReparseWorker>();
builder.Services.AddMemoryCache();

// ── 1. Bind config ────────────────────────────────────────────────────────────
var ragCfg = builder.Configuration
    .GetSection("RagFlow")
    .Get<RagFlowSettings>()!;

// ── 2. Register HttpClient for RAGFlow ────────────────────────────────────────
builder.Services.AddHttpClient<RagFlowService>(client =>
{
    client.BaseAddress = new Uri(ragCfg.BaseUrl);
    client.DefaultRequestHeaders.Accept
        .Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromMinutes(30);
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler { AllowAutoRedirect = true };
    if (builder.Environment.IsDevelopment())
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});

// ── 3. Razor Pages + Swagger ──────────────────────────────────────────────────
builder.Services.AddAuthentication("Cookies")
    .AddCookie("Cookies", options =>
    {
        options.Cookie.Name = ".RagFlowAuth";
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;   // resets the 7-day window on activity
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole("admin"));
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RAGFlow API Wrapper",
        Version = "v1",
        Description = "Browse and call RAGFlow endpoints via Swagger UI"
    });
});

var app = builder.Build();

// ── 4. Middleware ─────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RAGFlow API v1");
    c.RoutePrefix = "swagger";   // Swagger at /swagger, UI at /
});

app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/Login"));  // ← add this
app.UseRouting();
app.UseAuthentication();   // reads the auth cookie on every request
app.UseAuthorization();    // enforces [Authorize] attributes on page models
app.MapRazorPages();

// ── 5. API Endpoints ──────────────────────────────────────────────────────────
app.MapGet("/document-image/{imageId}", async (string imageId, RagFlowService svc) =>
{
    var (data, ct) = await svc.GetChunkImageAsync(imageId);
    return Results.File(data, ct);
})
.RequireAuthorization()
.WithName("GetChunkImage").WithSummary("Proxy a chunk page image from RAGFlow").WithTags("Documents");

app.MapPost("/test-ocr", async (IFormFile file, IParser parser) =>
{
    using var s = file.OpenReadStream();
    var markdown = await parser.ParseAsync(s, file.FileName, file.ContentType);
    return Results.Text(markdown, "text/plain; charset=utf-8");
})
.DisableAntiforgery()
.RequireAuthorization("AdminOnly")
.WithTags("Debug");


app.MapGet("/form-preview/{templateId}", async (string templateId, DocxFormFillerService filler) =>
{
    try
    {
        var result = await filler.GetPreviewAsync(templateId);
        if (result is null) return Results.NotFound();
        return Results.File(result.Value.Data, result.Value.ContentType);
    }
    catch { return Results.NotFound(); }
})
.RequireAuthorization();

app.MapGet("/document-file/{documentId}", (string documentId, IWebHostEnvironment env) =>
{
    var dir = Path.Combine(env.WebRootPath, "doc-cache");
    var match = Directory.Exists(dir)
        ? Directory.GetFiles(dir, documentId + ".*").FirstOrDefault()
        : null;
    if (match == null) return Results.NotFound();
    var ext = Path.GetExtension(match).TrimStart('.').ToLower();
    var ct = ext == "jpg" ? "image/jpeg" : $"image/{ext}";
    return Results.File(match, ct);
})
.RequireAuthorization();

app.MapGet("/system", async (RagFlowService svc, IConfiguration config, IHttpClientFactory factory) =>
{
    // ── 1. RAGFlow: what embedding model / parser / LLM is configured ─────
    var (embedding, layout, llm) = await svc.GetActiveModelsFromDatasetAsync();

    // ── 2. VLLM: what model is actually loaded ────────────────────────────
    string vllmModel = "unreachable";
    string vllmContextLen = "?";
    try
    {
        var vllmBase = config["DotsOcr:BaseUrl"]!;
        var http = factory.CreateClient();
        var res = await http.GetAsync($"{vllmBase}/v1/models");
        if (res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
            vllmModel = first.GetProperty("id").GetString() ?? "unknown";
            vllmContextLen = first.TryGetProperty("max_model_len", out var ml)
                ? ml.GetInt32().ToString() : "?";
        }
    }
    catch { /* VLLM offline — surface what we know */ }

    return Results.Ok(new
    {
        parser = new
        {
            engine = layout,                  // e.g. "DeepDOC"
            model = vllmModel,               // e.g. "/workspace/weights/dots.mocr"
            max_context_tokens = vllmContextLen // e.g. "36000"
        },
        embedder = new
        {
            model = embedding                   // e.g. "bge-m3@Ollama"
        },
        llm = new
        {
            model = llm                         // e.g. "gemini-2.5-flash@Gemini"
        },
        ragflow_base = config["RagFlow:BaseUrl"],
        vllm_base = config["DotsOcr:BaseUrl"]
    });
})
.RequireAuthorization("AdminOnly")
.WithName("SystemInfo")
.WithSummary("Show active parser, embedder, and LLM configuration")
.WithTags("System");

// ── 6. Ingestion job status ───────────────────────────────────────────────────
app.MapGet("/ingest-status/{jobId}", (string jobId, IngestionJobStore store) =>
{
    var s = store.Get(jobId);
    if (s is null) return Results.NotFound(new { error = "Unknown job" });
    return Results.Ok(new
    {
        jobId    = s.JobId,
        fileName = s.FileName,
        state    = s.State.ToString().ToLowerInvariant(),   // pending|processing|done|failed
        message  = s.Message
    });
})
.RequireAuthorization()
.WithName("IngestStatus")
.WithSummary("Poll the status of an async ingestion job")
.WithTags("Documents");

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("Cookies");
    return Results.Redirect("/Login");
});
app.MapGet("/datasets", async (RagFlowService svc,
                               [FromQuery] int page = 1,
                               [FromQuery] int pageSize = 10) =>
{
    var json = await svc.ListDatasetsAsync(page, pageSize);
    return Results.Content(json, "application/json");
})
.RequireAuthorization("AdminOnly")
.WithName("ListDatasets")
.WithSummary("List all RAGFlow datasets")
.WithTags("RagFlowApi");

app.Run();