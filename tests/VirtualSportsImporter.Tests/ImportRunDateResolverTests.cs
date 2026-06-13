using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Services;

namespace VirtualSportsImporter.Tests;

public sealed class ImportRunDateResolverTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 06, 13, 13, 52, 30, TimeSpan.Zero);

    [Fact]
    public void Resolve_Today_UsesTodayMidnightToCurrentReportHour()
    {
        var resolution = CreateResolver().Resolve(new ImportRunRequest
        {
            ClientCode = "EXAMPLE_CLIENT",
            Period = "today",
            DryRun = true
        });

        Assert.Equal("today", resolution.Period);
        Assert.Equal(new DateOnly(2026, 06, 13), resolution.BusinessDate);
        Assert.Equal(new DateTime(2026, 06, 13, 00, 00, 00), resolution.From);
        Assert.Equal(new DateTime(2026, 06, 13, 13, 00, 00), resolution.To);
    }

    [Fact]
    public void Resolve_Yesterday_UsesYesterdayMidnightToTodayMidnight()
    {
        var resolution = CreateResolver().Resolve(new ImportRunRequest
        {
            ClientCode = "EXAMPLE_CLIENT",
            Period = "yesterday",
            DryRun = true
        });

        Assert.Equal("yesterday", resolution.Period);
        Assert.Equal(new DateOnly(2026, 06, 12), resolution.BusinessDate);
        Assert.Equal(new DateTime(2026, 06, 12, 00, 00, 00), resolution.From);
        Assert.Equal(new DateTime(2026, 06, 13, 00, 00, 00), resolution.To);
    }

    [Fact]
    public void Resolve_Custom_UsesExplicitDateRange()
    {
        var resolution = CreateResolver().Resolve(new ImportRunRequest
        {
            ClientCode = "EXAMPLE_CLIENT",
            Period = "custom",
            FromDate = new DateOnly(2026, 06, 01),
            ToDate = new DateOnly(2026, 06, 13),
            DryRun = true
        });

        Assert.Equal("custom", resolution.Period);
        Assert.Equal(new DateOnly(2026, 06, 01), resolution.BusinessDate);
        Assert.Equal(new DateTime(2026, 06, 01, 00, 00, 00), resolution.From);
        Assert.Equal(new DateTime(2026, 06, 13, 00, 00, 00), resolution.To);
    }

    [Fact]
    public void Resolve_LegacyBusinessDate_UsesExistingMidnightToNextMidnightRange()
    {
        var resolution = CreateResolver().Resolve(new ImportRunRequest
        {
            ClientCode = "EXAMPLE_CLIENT",
            BusinessDate = new DateOnly(2026, 06, 12),
            DryRun = true
        });

        Assert.Null(resolution.Period);
        Assert.Equal(new DateOnly(2026, 06, 12), resolution.BusinessDate);
        Assert.Equal(new DateTime(2026, 06, 12, 00, 00, 00), resolution.From);
        Assert.Equal(new DateTime(2026, 06, 13, 00, 00, 00), resolution.To);
    }

    [Fact]
    public void Resolve_InvalidPeriod_ThrowsValidationException()
    {
        var exception = Assert.Throws<ImportRunDateValidationException>(() =>
            CreateResolver().Resolve(new ImportRunRequest
            {
                ClientCode = "EXAMPLE_CLIENT",
                Period = "last-week",
                DryRun = true
            }));

        Assert.Equal("period must be one of: today, yesterday, custom.", exception.Message);
    }

    [Fact]
    public void Resolve_CustomWithoutBothDates_ThrowsValidationException()
    {
        var exception = Assert.Throws<ImportRunDateValidationException>(() =>
            CreateResolver().Resolve(new ImportRunRequest
            {
                ClientCode = "EXAMPLE_CLIENT",
                Period = "custom",
                FromDate = new DateOnly(2026, 06, 01),
                DryRun = true
            }));

        Assert.Equal("fromDate and toDate are required when period is custom.", exception.Message);
    }

    [Fact]
    public void Resolve_CustomWithToBeforeFrom_ThrowsValidationException()
    {
        var exception = Assert.Throws<ImportRunDateValidationException>(() =>
            CreateResolver().Resolve(new ImportRunRequest
            {
                ClientCode = "EXAMPLE_CLIENT",
                Period = "custom",
                FromDate = new DateOnly(2026, 06, 13),
                ToDate = new DateOnly(2026, 06, 01),
                DryRun = true
            }));

        Assert.Equal("toDate must be greater than or equal to fromDate.", exception.Message);
    }

    private static ImportRunDateResolver CreateResolver()
    {
        return new ImportRunDateResolver(new FixedTimeProvider(FixedNow));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now.ToUniversalTime();
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
