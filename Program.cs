using VirtualSportsImporter.Worker.Options;
using VirtualSportsImporter.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddControllers();

builder.Services.Configure<VirtualSportsOptions>(
    builder.Configuration.GetSection(VirtualSportsOptions.SectionName));
builder.Services.Configure<WorkerSecurityOptions>(
    builder.Configuration.GetSection(WorkerSecurityOptions.SectionName));
builder.Services.AddSingleton<IReadOnlyList<ClientImportApiOptions>>(_ =>
    builder.Configuration
        .GetSection(ClientImportApiOptions.SectionName)
        .Get<List<ClientImportApiOptions>>() ?? []);

builder.Services.AddSingleton<VirtualSportsScraper>();
builder.Services.AddScoped<ImportJobRunner>();
builder.Services.AddHttpClient<ProductImportApiClient>();

var app = builder.Build();

app.MapControllers();

app.Run();
