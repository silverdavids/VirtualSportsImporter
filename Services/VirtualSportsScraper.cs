using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class VirtualSportsScraper
{
    private static readonly CultureInfo AmountCulture = CultureInfo.InvariantCulture;

    private readonly IOptions<VirtualSportsOptions> _options;
    private readonly ILogger<VirtualSportsScraper> _logger;

    public VirtualSportsScraper(
        IOptions<VirtualSportsOptions> options,
        ILogger<VirtualSportsScraper> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<VirtualSportsScrapeResult> ExtractRowsAsync(
        string clientCode,
        DateTime businessDate,
        DateTime fromDate,
        DateTime toDate,
        string? period,
        VirtualSportsOptions options,
        bool saveSuccessAudit,
        CancellationToken cancellationToken)
    {
        ValidateOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);
        AuditArtifactPaths? generatedReportArtifacts = null;
        DateRangeSelectionResult? dateRangeSelection = null;

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);
            await OpenGeneralOverviewReportAsync(page, clientCode, options.Selectors, cancellationToken);
            await SelectReportAgentAsync(page, clientCode, options, cancellationToken);
            dateRangeSelection = await SelectDateRangeAsync(
                page,
                clientCode,
                options.Selectors,
                businessDate,
                fromDate,
                toDate,
                period,
                cancellationToken);

            dateRangeSelection = await RetryWithAvailableRangeIfNeededAsync(
                page,
                options.Selectors,
                dateRangeSelection,
                cancellationToken);

            if (saveSuccessAudit)
            {
                generatedReportArtifacts = await SaveAuditArtifactsAsync(
                    page,
                    options.AuditPath,
                    businessDate,
                    "generated-report",
                    cancellationToken);
            }

            var portalReportError = await TryGetPortalReportErrorAsync(page, options.Selectors);

            if (!string.IsNullOrWhiteSpace(portalReportError))
            {
                if (await IsReportDataAvailableAsync(page, options.Selectors))
                {
                    _logger.LogWarning(
                        "VirtualSports portal report message ignored because report data is visible. ClientCode={ClientCode}, Message={PortalReportError}.",
                        clientCode,
                        portalReportError);
                }
                else
                {
                generatedReportArtifacts ??= await SaveAuditArtifactsAsync(
                    page,
                    options.AuditPath,
                    businessDate,
                    "portal-report-error",
                    cancellationToken);

                throw new VirtualSportsPortalReportException(
                    portalReportError,
                    dateRangeSelection.RequestedFromDateValue,
                    dateRangeSelection.RequestedToDateValue,
                    dateRangeSelection.ActualFromDateValue,
                    dateRangeSelection.ActualToDateValue,
                    dateRangeSelection.PortalAvailabilityMessage,
                    dateRangeSelection.RetriedWithAvailableRange,
                    generatedReportArtifacts.ScreenshotPath,
                    generatedReportArtifacts.HtmlPath);
                }
            }

            try
            {
                await ExpandShopsNodeAsync(page, clientCode, options.Selectors, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains(
                "ExpandShopsNodeSelector",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new VirtualSportsPortalReportException(
                    ex.Message,
                    dateRangeSelection.RequestedFromDateValue,
                    dateRangeSelection.RequestedToDateValue,
                    dateRangeSelection.ActualFromDateValue,
                    dateRangeSelection.ActualToDateValue,
                    dateRangeSelection.PortalAvailabilityMessage,
                    dateRangeSelection.RetriedWithAvailableRange,
                    generatedReportArtifacts?.ScreenshotPath ?? string.Empty,
                    generatedReportArtifacts?.HtmlPath ?? string.Empty);
            }

            var rows = await ExtractReportRowsAsync(page, clientCode, options.Selectors, businessDate);

            _logger.LogInformation(
                "VirtualSports scrape completed. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}, Rows={RowCount}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}, GeneratedReportScreenshotPath={GeneratedReportScreenshotPath}, GeneratedReportHtmlPath={GeneratedReportHtmlPath}.",
                clientCode,
                businessDate,
                rows.Count,
                dateRangeSelection.ActualFromDateValue,
                dateRangeSelection.ActualToDateValue,
                generatedReportArtifacts?.ScreenshotPath,
                generatedReportArtifacts?.HtmlPath);

            return new VirtualSportsScrapeResult
            {
                Rows = rows,
                RequestedFromDateValue = dateRangeSelection.RequestedFromDateValue,
                RequestedToDateValue = dateRangeSelection.RequestedToDateValue,
                ActualFromDateValue = dateRangeSelection.ActualFromDateValue,
                ActualToDateValue = dateRangeSelection.ActualToDateValue,
                PortalAvailabilityMessage = dateRangeSelection.PortalAvailabilityMessage,
                RetriedWithAvailableRange = dateRangeSelection.RetriedWithAvailableRange,
                GeneratedReportScreenshotPath = generatedReportArtifacts?.ScreenshotPath ?? string.Empty,
                GeneratedReportHtmlPath = generatedReportArtifacts?.HtmlPath ?? string.Empty
            };
        }
        catch (VirtualSportsPortalReportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureArtifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                businessDate,
                "failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports scrape failed. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}. Audit artifacts saved to {AuditPath}. GeneratedReportScreenshotPath={GeneratedReportScreenshotPath}, GeneratedReportHtmlPath={GeneratedReportHtmlPath}.",
                clientCode,
                businessDate,
                options.AuditPath,
                generatedReportArtifacts?.ScreenshotPath,
                generatedReportArtifacts?.HtmlPath);

            throw new VirtualSportsScrapeException(
                ex.Message,
                ex,
                dateRangeSelection?.RequestedFromDateValue ?? string.Empty,
                dateRangeSelection?.RequestedToDateValue ?? string.Empty,
                dateRangeSelection?.ActualFromDateValue ?? string.Empty,
                dateRangeSelection?.ActualToDateValue ?? string.Empty,
                dateRangeSelection?.PortalAvailabilityMessage ?? string.Empty,
                dateRangeSelection?.RetriedWithAvailableRange ?? false,
                generatedReportArtifacts?.ScreenshotPath ?? failureArtifacts.ScreenshotPath,
                generatedReportArtifacts?.HtmlPath ?? failureArtifacts.HtmlPath);
        }
    }

    public async Task<SelectorDiscoveryResult> DiscoverSelectorsAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateLoginOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);

            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "selector-discovery",
                cancellationToken);

            var result = new SelectorDiscoveryResult
            {
                Success = true,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = []
            };

            _logger.LogInformation(
                "VirtualSports selector discovery completed. ClientCode={ClientCode}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}.",
                result.ClientCode,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "selector-discovery-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports selector discovery failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<LoginTestResult> TestLoginAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateLoginOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);

            var errors = new List<string>();
            var success = await DetermineLoginSuccessAsync(page, options, errors);
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                success ? "login-test-success" : "login-test",
                cancellationToken);

            var result = new LoginTestResult
            {
                Success = success,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = errors
            };

            _logger.LogInformation(
                "VirtualSports login test completed. ClientCode={ClientCode}, Success={Success}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}, Errors={ErrorCount}.",
                result.ClientCode,
                result.Success,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath,
                result.Errors.Count);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "login-test-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports login test failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new LoginTestResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<SelectorDiscoveryResult> CaptureReportPageSnapshotAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateReportPageSnapshotOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);
            await OpenGeneralOverviewReportAsync(page, clientCode, options.Selectors, cancellationToken);

            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "report-page-snapshot",
                cancellationToken);

            var result = new SelectorDiscoveryResult
            {
                Success = true,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = []
            };

            _logger.LogInformation(
                "VirtualSports report page snapshot completed. ClientCode={ClientCode}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}.",
                result.ClientCode,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "report-page-snapshot-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports report page snapshot failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task LoginAsync(IPage page, CancellationToken cancellationToken = default)
    {
        await LoginAsync(page, "GLOBAL", _options.Value, cancellationToken);
    }

    private async Task LoginAsync(
        IPage page,
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        var selectors = options.Selectors;

        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Login");

        await page.GotoAsync(options.LoginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await FillRequiredAsync(page, selectors.Username, options.Username, "Username", cancellationToken);
        await FillRequiredAsync(page, selectors.Password, options.Password, "Password", cancellationToken);
        await SelectLoginRoleAsync(page, options, cancellationToken);
        await ClickRequiredAsync(page, selectors.LoginButton, "LoginButton", cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Login");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task OpenGeneralOverviewReportAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Open General Overview");

        if (TryGetConfiguredUrl(selectors.GeneralOverviewMenu, out var configuredUrl))
        {
            await NavigateToConfiguredUrlAsync(page, configuredUrl);
        }
        else
        {
            await ClickRequiredAsync(page, selectors.GeneralOverviewMenu, "GeneralOverviewMenu", cancellationToken);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Open General Overview");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<DateRangeSelectionResult> SelectDateRangeAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        DateTime businessDate,
        DateTime fromDate,
        DateTime toDate,
        string? period,
        CancellationToken cancellationToken)
    {
        var fromDateValue = fromDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var toDateValue = toDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var normalizedPeriod = period?.Trim().ToLowerInvariant();

        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDate={FromDate}, ToDate={ToDate}.",
            clientCode,
            "Select Date Range",
            businessDate,
            fromDateValue,
            toDateValue);

        if (normalizedPeriod is "today")
        {
            await SelectTimeframeAsync(
                page,
                selectors,
                selectors.TimeframeTodayText,
                "TimeframeTodayText",
                cancellationToken);
        }
        else if (normalizedPeriod is "yesterday")
        {
            await SelectTimeframeAsync(
                page,
                selectors,
                selectors.TimeframeYesterdayText,
                "TimeframeYesterdayText",
                cancellationToken);
        }
        else
        {
            await SelectTimeframeAsync(
                page,
                selectors,
                selectors.TimeframeCustomText,
                "TimeframeCustomText",
                cancellationToken,
                required: false);
            await FillDateAsync(
                page,
                selectors.FromDate,
                fromDateValue,
                "FromDate",
                selectors.DatePickerOkButton,
                cancellationToken);
            await FillDateAsync(
                page,
                selectors.ToDate,
                toDateValue,
                "ToDate",
                selectors.DatePickerOkButton,
                cancellationToken);
            await SetDateInputValueAsync(page, selectors.FromDate, fromDateValue, "FromDate");
            await SetDateInputValueAsync(page, selectors.ToDate, toDateValue, "ToDate");
            await page.Keyboard.PressAsync("Escape");
            await page.Keyboard.PressAsync("Escape");
        }

        _logger.LogInformation(
            "VirtualSports date selection applied. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}, Period={Period}.",
            clientCode,
            businessDate,
            normalizedPeriod ?? "legacy");

        await ClickGenerateReportAsync(page, selectors.SearchButton, cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForOptionalHiddenAsync(page, selectors.ReportLoadingIndicator);
        await page.WaitForTimeoutAsync(1000);

        var selectedFromDate = await ReadInputValueAsync(page, selectors.FromDate, "FromDate");
        var selectedToDate = await ReadInputValueAsync(page, selectors.ToDate, "ToDate");

        _logger.LogInformation(
            "VirtualSports date inputs read back. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}.",
            clientCode,
            businessDate,
            selectedFromDate,
            selectedToDate);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}.",
            clientCode,
            "Select Date Range",
            businessDate,
            selectedFromDate,
            selectedToDate);

        cancellationToken.ThrowIfCancellationRequested();

        return new DateRangeSelectionResult
        {
            RequestedFromDateValue = selectedFromDate,
            RequestedToDateValue = selectedToDate,
            ActualFromDateValue = selectedFromDate,
            ActualToDateValue = selectedToDate
        };
    }

    private async Task SelectReportAgentAsync(
        IPage page,
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        var selectors = options.Selectors;

        if (string.IsNullOrWhiteSpace(selectors.AgentInput))
        {
            return;
        }

        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Select Report Agent");

        await WaitForRequiredAsync(page, selectors.AgentInput, "AgentInput");

        var agentInput = page.Locator(selectors.AgentInput).First;
        await agentInput.ClickAsync();
        await agentInput.FillAsync(options.Username);
        await page.WaitForTimeoutAsync(500);

        if (!string.IsNullOrWhiteSpace(selectors.AgentOption))
        {
            await ClickRequiredAsync(page, selectors.AgentOption, "AgentOption", cancellationToken);
        }
        else
        {
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            await page.Keyboard.PressAsync("Tab");
        }

        await page.WaitForTimeoutAsync(500);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Select Report Agent");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<DateRangeSelectionResult> RetryWithAvailableRangeIfNeededAsync(
        IPage page,
        VirtualSportsSelectorsOptions selectors,
        DateRangeSelectionResult initialSelection,
        CancellationToken cancellationToken)
    {
        var availabilityRange = await TryGetPortalAvailabilityRangeAsync(page, selectors);

        if (availabilityRange is null)
        {
            return initialSelection;
        }

        _logger.LogInformation(
            "VirtualSports portal availability range detected. Message={PortalAvailabilityMessage}, AvailableFrom={AvailableFrom}, AvailableTo={AvailableTo}. Retrying with available range.",
            availabilityRange.Message,
            availabilityRange.AvailableFrom,
            availabilityRange.AvailableTo);

        await SelectTimeframeAsync(
            page,
            selectors,
            selectors.TimeframeCustomText,
            "TimeframeCustomText",
            cancellationToken,
            required: false);
        await FillDateAsync(
            page,
            selectors.FromDate,
            availabilityRange.AvailableFrom,
            "FromDate",
            selectors.DatePickerOkButton,
            cancellationToken);
        await FillDateAsync(
            page,
            selectors.ToDate,
            availabilityRange.AvailableTo,
            "ToDate",
            selectors.DatePickerOkButton,
            cancellationToken);
        await SetDateInputValueAsync(page, selectors.FromDate, availabilityRange.AvailableFrom, "FromDate");
        await SetDateInputValueAsync(page, selectors.ToDate, availabilityRange.AvailableTo, "ToDate");
        await page.Keyboard.PressAsync("Escape");
        await page.Keyboard.PressAsync("Escape");
        await ClickGenerateReportAsync(page, selectors.SearchButton, cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForOptionalHiddenAsync(page, selectors.ReportLoadingIndicator);
        await page.WaitForTimeoutAsync(1000);

        var actualFrom = await ReadInputValueAsync(page, selectors.FromDate, "FromDate");
        var actualTo = await ReadInputValueAsync(page, selectors.ToDate, "ToDate");

        return initialSelection with
        {
            ActualFromDateValue = actualFrom,
            ActualToDateValue = actualTo,
            PortalAvailabilityMessage = availabilityRange.Message,
            RetriedWithAvailableRange = true
        };
    }

    private async Task ExpandShopsNodeAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Expand Shops Node");

        await ClickRequiredAsync(
            page,
            selectors.ExpandShopsNodeSelector,
            "ExpandShopsNodeSelector",
            cancellationToken);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Expand Shops Node");
    }

    private async Task<IReadOnlyList<VirtualSportsImportRow>> ExtractReportRowsAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        DateTime businessDate)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Extract Rows");

        await WaitForRequiredAsync(page, selectors.ReportTable, "ReportTable");
        var tableRows = await page.Locator(selectors.ReportRows).AllAsync();
        var rows = new List<VirtualSportsImportRow>(tableRows.Count);

        foreach (var tableRow in tableRows)
        {
            var shopCode = await GetCellTextAsync(tableRow, selectors.ShopCodeCell, "ShopCodeCell");
            var shopName = await GetCellTextAsync(tableRow, selectors.ShopNameCell, "ShopNameCell");

            rows.Add(new VirtualSportsImportRow
            {
                ExternalShopCode = shopCode,
                ExternalShopName = string.IsNullOrWhiteSpace(shopName) ? shopCode : shopName,
                BusinessDate = businessDate.Date,
                Sales = ParseDecimal(await GetCellTextAsync(tableRow, selectors.TotalInCell, "TotalInCell")),
                Payout = ParseDecimal(await GetCellTextAsync(tableRow, selectors.TotalOutCell, "TotalOutCell")),
                TicketCount = ParseInt(await GetCellTextAsync(tableRow, selectors.TicketCountCell, "TicketCountCell"))
            });
        }

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}, Rows={RowCount}.",
            clientCode,
            "Extract Rows",
            rows.Count);

        return rows;
    }

    private static async Task<bool> IsReportDataAvailableAsync(
        IPage page,
        VirtualSportsSelectorsOptions selectors)
    {
        if (string.IsNullOrWhiteSpace(selectors.ExpandShopsNodeSelector))
        {
            return false;
        }

        try
        {
            await page.Locator(selectors.ExpandShopsNodeSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 1000
            });

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task FillRequiredAsync(
        IPage page,
        string selector,
        string value,
        string selectorName,
        CancellationToken cancellationToken)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        await page.FillAsync(selector, value);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task FillDateAsync(
        IPage page,
        string selector,
        string value,
        string selectorName,
        string datePickerOkButtonSelector,
        CancellationToken cancellationToken)
    {
        await WaitForRequiredAsync(page, selector, selectorName);

        var locator = page.Locator(selector).First;
        await locator.ClickAsync();
        await SetDateInputValueAsync(page, selector, value, selectorName);
        await ClickDatePickerDayAsync(page, value);
        await CommitDatePickerAsync(page, datePickerOkButtonSelector);
        await page.WaitForTimeoutAsync(250);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SelectTimeframeAsync(
        IPage page,
        VirtualSportsSelectorsOptions selectors,
        string timeframeText,
        string optionName,
        CancellationToken cancellationToken,
        bool required = true)
    {
        if (string.IsNullOrWhiteSpace(timeframeText))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    $"VirtualSports selector '{optionName}' must be configured before it can be used.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(selectors.TimeframeSelect))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "VirtualSports selector 'TimeframeSelect' must be configured before it can be used.");
            }

            return;
        }

        var select = await FindSelectWithOptionAsync(page, selectors.TimeframeSelect, timeframeText);

        try
        {
            await select.SelectOptionAsync(new[] { new SelectOptionValue { Label = timeframeText } });
        }
        catch (PlaywrightException)
        {
            await select.SelectOptionAsync(new[] { new SelectOptionValue { Value = timeframeText } });
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<ILocator> FindSelectWithOptionAsync(
        IPage page,
        string selector,
        string optionText)
    {
        await WaitForRequiredAsync(page, selector, "TimeframeSelect");

        var candidates = await page.Locator(selector).AllAsync();

        foreach (var candidate in candidates)
        {
            var hasOption = await candidate.EvaluateAsync<bool>(
                @"(select, optionText) => Array.from(select.options || [])
                    .some(option => option.text.trim() === optionText || option.value === optionText)",
                optionText);

            if (hasOption)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"VirtualSports timeframe option '{optionText}' was not found in any select matching selector: {selector}");
    }

    private static async Task SetDateInputValueAsync(
        IPage page,
        string selector,
        string value,
        string selectorName)
    {
        await WaitForRequiredAsync(page, selector, selectorName);

        var locator = page.Locator(selector).First;
        await locator.EvaluateAsync(
            @"(element, value) => {
                const input = element;
                const valueSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;

                if (valueSetter) {
                    valueSetter.call(input, value);
                } else {
                    input.value = value;
                }

                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                input.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, key: 'Enter' }));
                input.dispatchEvent(new Event('blur', { bubbles: false }));
            }",
            value);
    }

    private static async Task ClickDatePickerDayAsync(IPage page, string value)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return;
        }

        var day = date.Day.ToString(CultureInfo.InvariantCulture);
        var dayCell = page.Locator($"td:visible:text-is(\"{day}\")").Last;

        try
        {
            await dayCell.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000
            });
            await dayCell.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });
        }
        catch (PlaywrightException)
        {
            // The JavaScript assignment still gives us a useful audit trail if the picker UI changes.
        }
        catch (TimeoutException)
        {
            // The JavaScript assignment still gives us a useful audit trail if the picker UI changes.
        }
    }

    private static async Task CommitDatePickerAsync(IPage page, string datePickerOkButtonSelector)
    {
        if (string.IsNullOrWhiteSpace(datePickerOkButtonSelector))
        {
            await page.Keyboard.PressAsync("Escape");
            return;
        }

        var okButton = page.Locator(datePickerOkButtonSelector).Last;

        try
        {
            await okButton.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000
            });
            await okButton.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });
        }
        catch (PlaywrightException)
        {
            await page.Keyboard.PressAsync("Escape");
        }
    }

    private static async Task WaitForOptionalHiddenAsync(IPage page, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return;
        }

        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 45000
            });
        }
        catch (TimeoutException)
        {
            // Keep the saved audit artifacts useful even when a portal request hangs.
        }
        catch (PlaywrightException)
        {
            // Optional selectors should not hide the real extraction/configuration error.
        }
    }

    private static async Task<string> TryGetPortalReportErrorAsync(
        IPage page,
        VirtualSportsSelectorsOptions selectors)
    {
        var selector = string.IsNullOrWhiteSpace(selectors.ReportErrorMessage)
            ? "text=/available just for period|financial data is available|To Date must be after From Date/i"
            : selectors.ReportErrorMessage;

        try
        {
            var locator = page.Locator(selector).First;
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 500
            });

            return (await locator.InnerTextAsync()).Trim();
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private static async Task<PortalAvailabilityRange?> TryGetPortalAvailabilityRangeAsync(
        IPage page,
        VirtualSportsSelectorsOptions selectors)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var pageText = string.Empty;

            try
            {
                pageText = await page.Locator("body").InnerTextAsync();
            }
            catch (PlaywrightException)
            {
                // Fall back to the configured report-error selector below.
            }

            if (TryParseAvailableRange(
                    pageText,
                    out var portalAvailabilityMessage,
                    out var availableFrom,
                    out var availableTo))
            {
                return new PortalAvailabilityRange(portalAvailabilityMessage, availableFrom, availableTo);
            }

            var selectorMessage = await TryGetPortalReportErrorAsync(page, selectors);

            if (TryParseAvailableRange(
                    selectorMessage,
                    out portalAvailabilityMessage,
                    out availableFrom,
                    out availableTo))
            {
                return new PortalAvailabilityRange(portalAvailabilityMessage, availableFrom, availableTo);
            }

            await page.WaitForTimeoutAsync(500);
        }

        return null;
    }

    private static bool TryParseAvailableRange(
        string message,
        out string portalAvailabilityMessage,
        out string availableFrom,
        out string availableTo)
    {
        portalAvailabilityMessage = string.Empty;
        availableFrom = string.Empty;
        availableTo = string.Empty;

        var match = Regex.Match(
            message,
            @"financial\s+data\s+is\s+available\s+just\s+for\s+period\s+from\s+(?<from>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+to\s+(?<to>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        if (!match.Success)
        {
            match = Regex.Match(
                message,
                @"available\s+just\s+for\s+period\s+from\s+(?<from>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+to\s+(?<to>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            if (!match.Success)
            {
                return false;
            }
        }

        portalAvailabilityMessage = Regex.Replace(match.Value, @"\s+", " ").Trim();
        availableFrom = match.Groups["from"].Value;
        availableTo = match.Groups["to"].Value;
        return true;
    }

    private static async Task<string> ReadInputValueAsync(
        IPage page,
        string selector,
        string selectorName)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        return await page.Locator(selector).First.InputValueAsync();
    }

    private static async Task ClickRequiredAsync(
        IPage page,
        string selector,
        string selectorName,
        CancellationToken cancellationToken,
        bool force = false)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        await page.Locator(selector).First.ClickAsync(new LocatorClickOptions
        {
            Force = force
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ClickGenerateReportAsync(
        IPage page,
        string selector,
        CancellationToken cancellationToken)
    {
        await WaitForRequiredAsync(page, selector, "SearchButton");
        var button = page.Locator(selector).First;

        await button.ScrollIntoViewIfNeededAsync();
        await button.EvaluateAsync(
            @"element => {
                element.focus();
                element.click();
            }");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SelectLoginRoleAsync(
        IPage page,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.LoginRoleSelect) &&
            string.IsNullOrWhiteSpace(options.LoginRoleValue))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.LoginRoleSelect) ||
            string.IsNullOrWhiteSpace(options.LoginRoleValue))
        {
            throw new InvalidOperationException(
                "VirtualSports LoginRoleSelect and LoginRoleValue must both be configured when login role selection is used.");
        }

        await WaitForRequiredAsync(page, options.LoginRoleSelect, "LoginRoleSelect");
        await page.SelectOptionAsync(options.LoginRoleSelect, new SelectOptionValue
        {
            Label = options.LoginRoleValue
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetConfiguredUrl(string selectorOrUrl, out string configuredUrl)
    {
        const string prefix = "url:";

        if (selectorOrUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            configuredUrl = selectorOrUrl[prefix.Length..];
            return true;
        }

        configuredUrl = string.Empty;
        return false;
    }

    private static async Task NavigateToConfiguredUrlAsync(IPage page, string configuredUrl)
    {
        var currentUrl = new Uri(page.Url);
        var targetUrl = configuredUrl.StartsWith("#", StringComparison.Ordinal)
            ? new Uri(currentUrl.GetLeftPart(UriPartial.Path) + configuredUrl)
            : new Uri(currentUrl, configuredUrl);

        await page.GotoAsync(targetUrl.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
    }

    private static async Task WaitForRequiredAsync(IPage page, string selector, string selectorName)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' must be configured before it can be used.");
        }

        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' was not found or visible. Selector: {selector}",
                ex);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' lookup failed. Selector: {selector}",
                ex);
        }
    }

    private static async Task<string> GetCellTextAsync(
        ILocator tableRow,
        string selector,
        string selectorName)
    {
        try
        {
            var locator = tableRow.Locator(selector).First;
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached
            });

            return (await locator.InnerTextAsync()).Trim();
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' was not found in a report row. Selector: {selector}",
                ex);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' lookup failed in a report row. Selector: {selector}",
                ex);
        }
    }

    private static async Task<bool> DetermineLoginSuccessAsync(
        IPage page,
        VirtualSportsOptions options,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(options.LoginFailureSelector) &&
            await IsSelectorPresentAsync(page, options.LoginFailureSelector))
        {
            errors.Add(
                $"Login failure selector was found. Selector: {options.LoginFailureSelector}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.LoginSuccessSelector) &&
            await IsSelectorPresentAsync(page, options.LoginSuccessSelector))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(options.LoginSuccessUrlContains) &&
            page.Url.Contains(options.LoginSuccessUrlContains, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        errors.Add(
            "Login result is uncertain. Configure LoginSuccessSelector, LoginSuccessUrlContains, or LoginFailureSelector to make this deterministic.");
        return false;
    }

    private static async Task<bool> IsSelectorPresentAsync(IPage page, string selector)
    {
        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 1000
            });

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static decimal ParseDecimal(string value)
    {
        var cleaned = new string(value
            .Where(character => char.IsDigit(character) || character is '.' or ',' or '-')
            .ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return 0;
        }

        var lastComma = cleaned.LastIndexOf(',');
        var lastPeriod = cleaned.LastIndexOf('.');

        if (lastComma >= 0 && lastPeriod >= 0)
        {
            cleaned = lastComma > lastPeriod
                ? cleaned.Replace(".", string.Empty).Replace(',', '.')
                : cleaned.Replace(",", string.Empty);
        }
        else if (lastComma >= 0)
        {
            var decimalDigits = cleaned.Length - lastComma - 1;
            cleaned = decimalDigits == 2
                ? cleaned.Replace(',', '.')
                : cleaned.Replace(",", string.Empty);
        }

        return decimal.Parse(cleaned, NumberStyles.Number, AmountCulture);
    }

    private static int ParseInt(string value)
    {
        var cleaned = value.Replace(",", string.Empty).Trim();
        return int.Parse(cleaned, NumberStyles.Integer, AmountCulture);
    }

    private static async Task<AuditArtifactPaths> SaveAuditArtifactsAsync(
        IPage page,
        string auditPath,
        DateTime businessDate,
        string reason,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var prefix = Path.Combine(auditPath, $"virtualsports-{businessDate:yyyyMMdd}-{reason}-{timestamp}");

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"{prefix}.png",
            FullPage = true
        });

        var html = await page.ContentAsync();
        await File.WriteAllTextAsync($"{prefix}.html", html, cancellationToken);

        return new AuditArtifactPaths
        {
            ScreenshotPath = $"{prefix}.png",
            HtmlPath = $"{prefix}.html"
        };
    }

    private static void ValidateOptions(string clientCode, VirtualSportsOptions options)
    {
        ValidateLoginOptions(clientCode, options);

        ValidateSelector(clientCode, nameof(options.Selectors.GeneralOverviewMenu), options.Selectors.GeneralOverviewMenu);
        ValidateSelector(clientCode, nameof(options.Selectors.FromDate), options.Selectors.FromDate);
        ValidateSelector(clientCode, nameof(options.Selectors.ToDate), options.Selectors.ToDate);
        ValidateSelector(clientCode, nameof(options.Selectors.SearchButton), options.Selectors.SearchButton);
    }

    private static void ValidateLoginOptions(string clientCode, VirtualSportsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LoginUrl) || options.LoginUrl == "https://...")
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:LoginUrl must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Username must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Password must be configured before running imports.");
        }

        ValidateSelector(clientCode, nameof(options.Selectors.Username), options.Selectors.Username);
        ValidateSelector(clientCode, nameof(options.Selectors.Password), options.Selectors.Password);
        ValidateSelector(clientCode, nameof(options.Selectors.LoginButton), options.Selectors.LoginButton);
    }

    private static void ValidateReportPageSnapshotOptions(string clientCode, VirtualSportsOptions options)
    {
        ValidateLoginOptions(clientCode, options);
        ValidateSelector(clientCode, nameof(options.Selectors.GeneralOverviewMenu), options.Selectors.GeneralOverviewMenu);
    }

    private static void ValidateSelector(string clientCode, string selectorName, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Selectors:{selectorName} must be configured before running imports.");
        }
    }
}

public sealed class SelectorDiscoveryResult
{
    public bool Success { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public string PageTitle { get; init; } = string.Empty;

    public string CurrentUrl { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class LoginTestResult
{
    public bool Success { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public string PageTitle { get; init; } = string.Empty;

    public string CurrentUrl { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class VirtualSportsScrapeResult
{
    public IReadOnlyCollection<VirtualSportsImportRow> Rows { get; init; } = [];

    public string RequestedFromDateValue { get; init; } = string.Empty;

    public string RequestedToDateValue { get; init; } = string.Empty;

    public string ActualFromDateValue { get; init; } = string.Empty;

    public string ActualToDateValue { get; init; } = string.Empty;

    public string FromDateValue => ActualFromDateValue;

    public string ToDateValue => ActualToDateValue;

    public string PortalAvailabilityMessage { get; init; } = string.Empty;

    public bool RetriedWithAvailableRange { get; init; }

    public string GeneratedReportScreenshotPath { get; init; } = string.Empty;

    public string GeneratedReportHtmlPath { get; init; } = string.Empty;
}

public sealed class VirtualSportsScrapeException : Exception
{
    public VirtualSportsScrapeException(
        string message,
        Exception innerException,
        string requestedFromDateValue,
        string requestedToDateValue,
        string actualFromDateValue,
        string actualToDateValue,
        string portalAvailabilityMessage,
        bool retriedWithAvailableRange,
        string generatedReportScreenshotPath,
        string generatedReportHtmlPath)
        : base(message, innerException)
    {
        RequestedFromDateValue = requestedFromDateValue;
        RequestedToDateValue = requestedToDateValue;
        ActualFromDateValue = actualFromDateValue;
        ActualToDateValue = actualToDateValue;
        PortalAvailabilityMessage = portalAvailabilityMessage;
        RetriedWithAvailableRange = retriedWithAvailableRange;
        GeneratedReportScreenshotPath = generatedReportScreenshotPath;
        GeneratedReportHtmlPath = generatedReportHtmlPath;
    }

    public string RequestedFromDateValue { get; }

    public string RequestedToDateValue { get; }

    public string ActualFromDateValue { get; }

    public string ActualToDateValue { get; }

    public string FromDateValue => ActualFromDateValue;

    public string ToDateValue => ActualToDateValue;

    public string PortalAvailabilityMessage { get; }

    public bool RetriedWithAvailableRange { get; }

    public string GeneratedReportScreenshotPath { get; }

    public string GeneratedReportHtmlPath { get; }
}

public sealed class VirtualSportsPortalReportException : Exception
{
    public VirtualSportsPortalReportException(
        string message,
        string requestedFromDateValue,
        string requestedToDateValue,
        string actualFromDateValue,
        string actualToDateValue,
        string portalAvailabilityMessage,
        bool retriedWithAvailableRange,
        string generatedReportScreenshotPath,
        string generatedReportHtmlPath)
        : base(message)
    {
        RequestedFromDateValue = requestedFromDateValue;
        RequestedToDateValue = requestedToDateValue;
        ActualFromDateValue = actualFromDateValue;
        ActualToDateValue = actualToDateValue;
        PortalAvailabilityMessage = portalAvailabilityMessage;
        RetriedWithAvailableRange = retriedWithAvailableRange;
        GeneratedReportScreenshotPath = generatedReportScreenshotPath;
        GeneratedReportHtmlPath = generatedReportHtmlPath;
    }

    public string RequestedFromDateValue { get; }

    public string RequestedToDateValue { get; }

    public string ActualFromDateValue { get; }

    public string ActualToDateValue { get; }

    public string FromDateValue => ActualFromDateValue;

    public string ToDateValue => ActualToDateValue;

    public string PortalAvailabilityMessage { get; }

    public bool RetriedWithAvailableRange { get; }

    public string GeneratedReportScreenshotPath { get; }

    public string GeneratedReportHtmlPath { get; }
}

public sealed record DateRangeSelectionResult
{
    public string RequestedFromDateValue { get; init; } = string.Empty;

    public string RequestedToDateValue { get; init; } = string.Empty;

    public string ActualFromDateValue { get; init; } = string.Empty;

    public string ActualToDateValue { get; init; } = string.Empty;

    public string PortalAvailabilityMessage { get; init; } = string.Empty;

    public bool RetriedWithAvailableRange { get; init; }
}

public sealed record PortalAvailabilityRange(
    string Message,
    string AvailableFrom,
    string AvailableTo);

public sealed class AuditArtifactPaths
{
    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;
}
