using Microsoft.Extensions.Options;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class ImportJobRunner
{
    private readonly VirtualSportsScraper _scraper;
    private readonly ProductImportApiClient _productImportApiClient;
    private readonly IOptions<VirtualSportsOptions> _globalVirtualSportsOptions;
    private readonly ImportRunDateResolver _dateResolver;
    private readonly ILogger<ImportJobRunner> _logger;

    public ImportJobRunner(
        VirtualSportsScraper scraper,
        ProductImportApiClient productImportApiClient,
        IOptions<VirtualSportsOptions> globalVirtualSportsOptions,
        ImportRunDateResolver dateResolver,
        ILogger<ImportJobRunner> logger)
    {
        _scraper = scraper;
        _productImportApiClient = productImportApiClient;
        _globalVirtualSportsOptions = globalVirtualSportsOptions;
        _dateResolver = dateResolver;
        _logger = logger;
    }

    public async Task<ImportRunResult> RunAsync(
        string clientCode,
        ImportRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        ImportRunDateResolution? dateResolution = null;
        var businessDate = DateTime.MinValue;

        try
        {
            var client = _productImportApiClient.GetClientOrThrow(clientCode);
            var virtualSportsOptions = BuildVirtualSportsOptions(client);
            dateResolution = _dateResolver.Resolve(request, virtualSportsOptions);
            businessDate = dateResolution.BusinessDate.ToDateTime(TimeOnly.MinValue);
            var scrapeResult = await _scraper.ExtractRowsAsync(
                client.ClientCode,
                businessDate,
                dateResolution.From,
                dateResolution.To,
                dateResolution.Period,
                virtualSportsOptions,
                request.DryRun,
                cancellationToken);
            var rows = scrapeResult.Rows;

            if (!request.DryRun)
            {
                await _productImportApiClient.PostBulkAsync(client, businessDate.Date, rows, cancellationToken);
            }

            var result = ImportRunResult.Successful(
                client.ClientCode,
                businessDate.Date,
                request.DryRun,
                dateResolution.Period,
                rows,
                scrapeResult.RequestedFromDateValue,
                scrapeResult.RequestedToDateValue,
                scrapeResult.ActualFromDateValue,
                scrapeResult.ActualToDateValue,
                scrapeResult.PortalAvailabilityMessage,
                scrapeResult.RetriedWithAvailableRange,
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
        catch (ImportRunDateValidationException)
        {
            throw;
        }
        catch (VirtualSportsPortalReportException ex)
        {
            errors.Add(ex.Message);

            _logger.LogWarning(
                ex,
                "Product import run stopped by portal report message. ClientCode={ClientCode}, DryRun={DryRun}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}, Errors={ErrorCount}",
                clientCode,
                request.DryRun,
                businessDate.Date,
                ex.ActualFromDateValue,
                ex.ActualToDateValue,
                errors.Count);

            return ImportRunResult.Failed(
                clientCode,
                businessDate.Date,
                request.DryRun,
                dateResolution?.Period,
                errors,
                ex.RequestedFromDateValue,
                ex.RequestedToDateValue,
                ex.ActualFromDateValue,
                ex.ActualToDateValue,
                ex.PortalAvailabilityMessage,
                ex.RetriedWithAvailableRange,
                ex.GeneratedReportScreenshotPath,
                ex.GeneratedReportHtmlPath,
                isRecoverableFailure: true);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            var generatedReportScreenshotPath = string.Empty;
            var generatedReportHtmlPath = string.Empty;
            var requestedFromDateValue = string.Empty;
            var requestedToDateValue = string.Empty;
            var actualFromDateValue = string.Empty;
            var actualToDateValue = string.Empty;
            var portalAvailabilityMessage = string.Empty;
            var retriedWithAvailableRange = false;

            if (ex is VirtualSportsScrapeException scrapeException)
            {
                generatedReportScreenshotPath = scrapeException.GeneratedReportScreenshotPath;
                generatedReportHtmlPath = scrapeException.GeneratedReportHtmlPath;
                requestedFromDateValue = scrapeException.RequestedFromDateValue;
                requestedToDateValue = scrapeException.RequestedToDateValue;
                actualFromDateValue = scrapeException.ActualFromDateValue;
                actualToDateValue = scrapeException.ActualToDateValue;
                portalAvailabilityMessage = scrapeException.PortalAvailabilityMessage;
                retriedWithAvailableRange = scrapeException.RetriedWithAvailableRange;
            }

            _logger.LogError(
                ex,
                "Product import run failed. ClientCode={ClientCode}, DryRun={DryRun}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}, Rows=0, TotalSales=0, TotalPayout=0, TotalTickets=0, Errors={ErrorCount}",
                clientCode,
                request.DryRun,
                businessDate.Date,
                actualFromDateValue,
                actualToDateValue,
                errors.Count);

            return ImportRunResult.Failed(
                clientCode,
                businessDate.Date,
                request.DryRun,
                dateResolution?.Period,
                errors,
                requestedFromDateValue,
                requestedToDateValue,
                actualFromDateValue,
                actualToDateValue,
                portalAvailabilityMessage,
                retriedWithAvailableRange,
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
                ReportTimeZone = global.ReportTimeZone,
                TodayToMode = global.TodayToMode,
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
            ReportTimeZone = FirstConfigured(clientVirtualSports.ReportTimeZone, global.ReportTimeZone),
            TodayToMode = FirstConfigured(clientVirtualSports.TodayToMode, global.TodayToMode),
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
            AgentInput = FirstConfigured(primary.AgentInput, fallback.AgentInput),
            AgentOption = FirstConfigured(primary.AgentOption, fallback.AgentOption),
            FromDate = FirstConfigured(primary.FromDate, fallback.FromDate),
            ToDate = FirstConfigured(primary.ToDate, fallback.ToDate),
            TimeframeSelect = FirstConfigured(primary.TimeframeSelect, fallback.TimeframeSelect),
            TimeframeTodayText = FirstConfigured(primary.TimeframeTodayText, fallback.TimeframeTodayText),
            TimeframeYesterdayText = FirstConfigured(
                primary.TimeframeYesterdayText,
                fallback.TimeframeYesterdayText),
            TimeframeCustomText = FirstConfigured(primary.TimeframeCustomText, fallback.TimeframeCustomText),
            DatePickerOkButton = FirstConfigured(primary.DatePickerOkButton, fallback.DatePickerOkButton),
            SearchButton = FirstConfigured(primary.SearchButton, fallback.SearchButton),
            ReportLoadingIndicator = FirstConfigured(
                primary.ReportLoadingIndicator,
                fallback.ReportLoadingIndicator),
            ReportErrorMessage = FirstConfigured(primary.ReportErrorMessage, fallback.ReportErrorMessage),
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

    public bool IsRecoverableFailure { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public bool DryRun { get; init; }

    public string? Period { get; init; }

    public DateTime BusinessDate { get; init; }

    public int RowCount { get; init; }

    public int RowsImported { get; init; }

    public decimal TotalSales { get; init; }

    public decimal TotalPayout { get; init; }

    public int TotalTickets { get; init; }

    public string FromDateValue { get; init; } = string.Empty;

    public string ToDateValue { get; init; } = string.Empty;

    public string RequestedFromDateValue { get; init; } = string.Empty;

    public string RequestedToDateValue { get; init; } = string.Empty;

    public string ActualFromDateValue { get; init; } = string.Empty;

    public string ActualToDateValue { get; init; } = string.Empty;

    public string PortalAvailabilityMessage { get; init; } = string.Empty;

    public bool RetriedWithAvailableRange { get; init; }

    public string GeneratedReportScreenshotPath { get; init; } = string.Empty;

    public string GeneratedReportHtmlPath { get; init; } = string.Empty;

    public IReadOnlyCollection<VirtualSportsImportRow> Rows { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ImportRunResult Successful(
        string clientCode,
        DateTime businessDate,
        bool dryRun,
        string? period,
        IReadOnlyCollection<VirtualSportsImportRow> rows,
        string requestedFromDateValue,
        string requestedToDateValue,
        string actualFromDateValue,
        string actualToDateValue,
        string portalAvailabilityMessage,
        bool retriedWithAvailableRange,
        string generatedReportScreenshotPath,
        string generatedReportHtmlPath)
    {
        return new ImportRunResult
        {
            Success = true,
            ClientCode = clientCode,
            DryRun = dryRun,
            Period = period,
            BusinessDate = businessDate.Date,
            RowCount = rows.Count,
            RowsImported = dryRun ? 0 : rows.Count,
            TotalSales = rows.Sum(row => row.Sales),
            TotalPayout = rows.Sum(row => row.Payout),
            TotalTickets = rows.Sum(row => row.TicketCount),
            FromDateValue = actualFromDateValue,
            ToDateValue = actualToDateValue,
            RequestedFromDateValue = requestedFromDateValue,
            RequestedToDateValue = requestedToDateValue,
            ActualFromDateValue = actualFromDateValue,
            ActualToDateValue = actualToDateValue,
            PortalAvailabilityMessage = portalAvailabilityMessage,
            RetriedWithAvailableRange = retriedWithAvailableRange,
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
        string? period,
        IReadOnlyList<string> errors,
        string requestedFromDateValue = "",
        string requestedToDateValue = "",
        string actualFromDateValue = "",
        string actualToDateValue = "",
        string portalAvailabilityMessage = "",
        bool retriedWithAvailableRange = false,
        string generatedReportScreenshotPath = "",
        string generatedReportHtmlPath = "",
        bool isRecoverableFailure = false)
    {
        return new ImportRunResult
        {
            Success = false,
            IsRecoverableFailure = isRecoverableFailure,
            ClientCode = clientCode,
            DryRun = dryRun,
            Period = period,
            BusinessDate = businessDate.Date,
            RowCount = 0,
            RowsImported = 0,
            TotalSales = 0,
            TotalPayout = 0,
            TotalTickets = 0,
            FromDateValue = actualFromDateValue,
            ToDateValue = actualToDateValue,
            RequestedFromDateValue = requestedFromDateValue,
            RequestedToDateValue = requestedToDateValue,
            ActualFromDateValue = actualFromDateValue,
            ActualToDateValue = actualToDateValue,
            PortalAvailabilityMessage = portalAvailabilityMessage,
            RetriedWithAvailableRange = retriedWithAvailableRange,
            GeneratedReportScreenshotPath = generatedReportScreenshotPath,
            GeneratedReportHtmlPath = generatedReportHtmlPath,
            Rows = [],
            Errors = errors
        };
    }
}
