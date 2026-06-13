using Microsoft.Extensions.Options;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class ImportJobRunner
{
    private readonly VirtualSportsScraper _scraper;
    private readonly ProductImportApiClient _productImportApiClient;
    private readonly IOptions<VirtualSportsOptions> _globalVirtualSportsOptions;
    private readonly ILogger<ImportJobRunner> _logger;

    public ImportJobRunner(
        VirtualSportsScraper scraper,
        ProductImportApiClient productImportApiClient,
        IOptions<VirtualSportsOptions> globalVirtualSportsOptions,
        ILogger<ImportJobRunner> logger)
    {
        _scraper = scraper;
        _productImportApiClient = productImportApiClient;
        _globalVirtualSportsOptions = globalVirtualSportsOptions;
        _logger = logger;
    }

    public async Task<ImportRunResult> RunAsync(
        string clientCode,
        DateTime businessDate,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        try
        {
            var client = _productImportApiClient.GetClientOrThrow(clientCode);
            var virtualSportsOptions = BuildVirtualSportsOptions(client);
            var scrapeResult = await _scraper.ExtractRowsAsync(
                client.ClientCode,
                businessDate.Date,
                virtualSportsOptions,
                dryRun,
                cancellationToken);
            var rows = scrapeResult.Rows;

            if (!dryRun)
            {
                await _productImportApiClient.PostBulkAsync(client, businessDate.Date, rows, cancellationToken);
            }

            var result = ImportRunResult.Successful(
                client.ClientCode,
                businessDate.Date,
                dryRun,
                rows,
                scrapeResult.FromDateValue,
                scrapeResult.ToDateValue,
                scrapeResult.GeneratedReportScreenshotPath,
                scrapeResult.GeneratedReportHtmlPath);

            _logger.LogInformation(
                "Product import run completed. ClientCode={ClientCode}, DryRun={DryRun}, BusinessDate={BusinessDate:yyyy-MM-dd}, Rows={RowCount}, TotalSales={TotalSales}, TotalPayout={TotalPayout}, TotalTickets={TotalTickets}, Errors={ErrorCount}",
                result.ClientCode,
                result.DryRun,
                result.BusinessDate,
                result.RowCount,
                result.TotalSales,
                result.TotalPayout,
                result.TotalTickets,
                result.Errors.Count);

            return result;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            var generatedReportScreenshotPath = string.Empty;
            var generatedReportHtmlPath = string.Empty;
            var fromDateValue = string.Empty;
            var toDateValue = string.Empty;

            if (ex is VirtualSportsScrapeException scrapeException)
            {
                generatedReportScreenshotPath = scrapeException.GeneratedReportScreenshotPath;
                generatedReportHtmlPath = scrapeException.GeneratedReportHtmlPath;
                fromDateValue = scrapeException.FromDateValue;
                toDateValue = scrapeException.ToDateValue;
            }

            _logger.LogError(
                ex,
                "Product import run failed. ClientCode={ClientCode}, DryRun={DryRun}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}, Rows=0, TotalSales=0, TotalPayout=0, TotalTickets=0, Errors={ErrorCount}",
                clientCode,
                dryRun,
                businessDate.Date,
                fromDateValue,
                toDateValue,
                errors.Count);

            return ImportRunResult.Failed(
                clientCode,
                businessDate.Date,
                dryRun,
                errors,
                fromDateValue,
                toDateValue,
                generatedReportScreenshotPath,
                generatedReportHtmlPath);
        }
    }

    public async Task<SelectorDiscoveryResult> DiscoverSelectorsAsync(
        string clientCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _productImportApiClient.GetClientOrThrow(clientCode);
            var virtualSportsOptions = BuildVirtualSportsOptions(client);

            return await _scraper.DiscoverSelectorsAsync(
                client.ClientCode,
                virtualSportsOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Product import selector discovery failed. ClientCode={ClientCode}, Errors=1",
                clientCode);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<LoginTestResult> TestLoginAsync(
        string clientCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _productImportApiClient.GetClientOrThrow(clientCode);
            var virtualSportsOptions = BuildVirtualSportsOptions(client);

            return await _scraper.TestLoginAsync(
                client.ClientCode,
                virtualSportsOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Product import login test failed. ClientCode={ClientCode}, Errors=1",
                clientCode);

            return new LoginTestResult
            {
                Success = false,
                ClientCode = clientCode,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<SelectorDiscoveryResult> CaptureReportPageSnapshotAsync(
        string clientCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _productImportApiClient.GetClientOrThrow(clientCode);
            var virtualSportsOptions = BuildVirtualSportsOptions(client);

            return await _scraper.CaptureReportPageSnapshotAsync(
                client.ClientCode,
                virtualSportsOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Product import report page snapshot failed. ClientCode={ClientCode}, Errors=1",
                clientCode);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                Errors = [ex.Message]
            };
        }
    }

    private VirtualSportsOptions BuildVirtualSportsOptions(ClientImportApiOptions client)
    {
        var global = _globalVirtualSportsOptions.Value;
        var clientVirtualSports = client.VirtualSports;

        if (clientVirtualSports is null)
        {
            return new VirtualSportsOptions
            {
                LoginUrl = global.LoginUrl,
                Username = global.Username,
                Password = global.Password,
                Headless = global.Headless,
                TimeoutSeconds = global.TimeoutSeconds,
                AuditPath = global.AuditPath,
                LoginSuccessUrlContains = global.LoginSuccessUrlContains,
                LoginSuccessSelector = global.LoginSuccessSelector,
                LoginFailureSelector = global.LoginFailureSelector,
                LoginRoleSelect = global.LoginRoleSelect,
                LoginRoleValue = global.LoginRoleValue,
                Selectors = CopySelectors(global.Selectors)
            };
        }

        return new VirtualSportsOptions
        {
            LoginUrl = FirstConfigured(clientVirtualSports.LoginUrl, global.LoginUrl),
            Username = FirstConfigured(clientVirtualSports.Username, global.Username),
            Password = FirstConfigured(clientVirtualSports.Password, global.Password),
            Headless = clientVirtualSports.Headless,
            TimeoutSeconds = clientVirtualSports.TimeoutSeconds > 0
                ? clientVirtualSports.TimeoutSeconds
                : global.TimeoutSeconds,
            AuditPath = FirstConfigured(clientVirtualSports.AuditPath, global.AuditPath),
            LoginSuccessUrlContains = FirstConfigured(
                clientVirtualSports.LoginSuccessUrlContains,
                global.LoginSuccessUrlContains),
            LoginSuccessSelector = FirstConfigured(
                clientVirtualSports.LoginSuccessSelector,
                global.LoginSuccessSelector),
            LoginFailureSelector = FirstConfigured(
                clientVirtualSports.LoginFailureSelector,
                global.LoginFailureSelector),
            LoginRoleSelect = FirstConfigured(
                clientVirtualSports.LoginRoleSelect,
                global.LoginRoleSelect),
            LoginRoleValue = FirstConfigured(
                clientVirtualSports.LoginRoleValue,
                global.LoginRoleValue),
            Selectors = MergeSelectors(clientVirtualSports.Selectors, global.Selectors)
        };
    }

    private static string FirstConfigured(string primary, string fallback)
    {
        return string.IsNullOrWhiteSpace(primary) ? fallback : primary;
    }

    private static VirtualSportsSelectorsOptions MergeSelectors(
        VirtualSportsSelectorsOptions primary,
        VirtualSportsSelectorsOptions fallback)
    {
        return new VirtualSportsSelectorsOptions
        {
            Username = FirstConfigured(primary.Username, fallback.Username),
            Password = FirstConfigured(primary.Password, fallback.Password),
            LoginButton = FirstConfigured(primary.LoginButton, fallback.LoginButton),
            GeneralOverviewMenu = FirstConfigured(primary.GeneralOverviewMenu, fallback.GeneralOverviewMenu),
            ExpandShopsNodeSelector = FirstConfigured(
                primary.ExpandShopsNodeSelector,
                fallback.ExpandShopsNodeSelector),
            FromDate = FirstConfigured(primary.FromDate, fallback.FromDate),
            ToDate = FirstConfigured(primary.ToDate, fallback.ToDate),
            DatePickerOkButton = FirstConfigured(primary.DatePickerOkButton, fallback.DatePickerOkButton),
            SearchButton = FirstConfigured(primary.SearchButton, fallback.SearchButton),
            ReportLoadingIndicator = FirstConfigured(
                primary.ReportLoadingIndicator,
                fallback.ReportLoadingIndicator),
            ReportTable = FirstConfigured(primary.ReportTable, fallback.ReportTable),
            ReportRows = FirstConfigured(primary.ReportRows, fallback.ReportRows),
            ShopCodeCell = FirstConfigured(primary.ShopCodeCell, fallback.ShopCodeCell),
            ShopNameCell = FirstConfigured(primary.ShopNameCell, fallback.ShopNameCell),
            TicketCountCell = FirstConfigured(primary.TicketCountCell, fallback.TicketCountCell),
            TotalInCell = FirstConfigured(primary.TotalInCell, fallback.TotalInCell),
            TotalOutCell = FirstConfigured(primary.TotalOutCell, fallback.TotalOutCell)
        };
    }

    private static VirtualSportsSelectorsOptions CopySelectors(VirtualSportsSelectorsOptions selectors)
    {
        return MergeSelectors(selectors, new VirtualSportsSelectorsOptions());
    }
}

public sealed class ImportRunResult
{
    public bool Success { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public bool DryRun { get; init; }

    public DateTime BusinessDate { get; init; }

    public int RowCount { get; init; }

    public int RowsImported { get; init; }

    public decimal TotalSales { get; init; }

    public decimal TotalPayout { get; init; }

    public int TotalTickets { get; init; }

    public string FromDateValue { get; init; } = string.Empty;

    public string ToDateValue { get; init; } = string.Empty;

    public string GeneratedReportScreenshotPath { get; init; } = string.Empty;

    public string GeneratedReportHtmlPath { get; init; } = string.Empty;

    public IReadOnlyCollection<VirtualSportsImportRow> Rows { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ImportRunResult Successful(
        string clientCode,
        DateTime businessDate,
        bool dryRun,
        IReadOnlyCollection<VirtualSportsImportRow> rows,
        string fromDateValue,
        string toDateValue,
        string generatedReportScreenshotPath,
        string generatedReportHtmlPath)
    {
        return new ImportRunResult
        {
            Success = true,
            ClientCode = clientCode,
            DryRun = dryRun,
            BusinessDate = businessDate.Date,
            RowCount = rows.Count,
            RowsImported = dryRun ? 0 : rows.Count,
            TotalSales = rows.Sum(row => row.Sales),
            TotalPayout = rows.Sum(row => row.Payout),
            TotalTickets = rows.Sum(row => row.TicketCount),
            FromDateValue = fromDateValue,
            ToDateValue = toDateValue,
            GeneratedReportScreenshotPath = generatedReportScreenshotPath,
            GeneratedReportHtmlPath = generatedReportHtmlPath,
            Rows = rows,
            Errors = []
        };
    }

    public static ImportRunResult Failed(
        string clientCode,
        DateTime businessDate,
        bool dryRun,
        IReadOnlyList<string> errors,
        string fromDateValue = "",
        string toDateValue = "",
        string generatedReportScreenshotPath = "",
        string generatedReportHtmlPath = "")
    {
        return new ImportRunResult
        {
            Success = false,
            ClientCode = clientCode,
            DryRun = dryRun,
            BusinessDate = businessDate.Date,
            RowCount = 0,
            RowsImported = 0,
            TotalSales = 0,
            TotalPayout = 0,
            TotalTickets = 0,
            FromDateValue = fromDateValue,
            ToDateValue = toDateValue,
            GeneratedReportScreenshotPath = generatedReportScreenshotPath,
            GeneratedReportHtmlPath = generatedReportHtmlPath,
            Rows = [],
            Errors = errors
        };
    }
}
