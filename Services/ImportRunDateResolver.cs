using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class ImportRunDateResolver
{
    private readonly TimeProvider _timeProvider;

    public ImportRunDateResolver(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ImportRunDateResolution Resolve(
        ImportRunRequest request,
        VirtualSportsOptions virtualSportsOptions)
    {
        if (string.IsNullOrWhiteSpace(request.Period))
        {
            return ResolveLegacyBusinessDate(request);
        }

        var period = request.Period.Trim().ToLowerInvariant();
        var reportTimeZone = ResolveTimeZone(virtualSportsOptions.ReportTimeZone);
        var now = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), reportTimeZone).DateTime;
        var today = DateOnly.FromDateTime(now);

        return period switch
        {
            "today" => new ImportRunDateResolution(
                Period: "today",
                BusinessDate: today,
                From: today.ToDateTime(TimeOnly.MinValue),
                To: ResolveTodayTo(now, virtualSportsOptions.TodayToMode)),
            "yesterday" => new ImportRunDateResolution(
                Period: "yesterday",
                BusinessDate: today.AddDays(-1),
                From: today.AddDays(-1).ToDateTime(TimeOnly.MinValue),
                To: today.ToDateTime(TimeOnly.MinValue)),
            "custom" => ResolveCustom(request),
            _ => throw new ImportRunDateValidationException(
                "period must be one of: today, yesterday, custom.")
        };
    }

    public ImportRunDateResolution Resolve(ImportRunRequest request)
    {
        return Resolve(request, new VirtualSportsOptions());
    }

    private static ImportRunDateResolution ResolveLegacyBusinessDate(ImportRunRequest request)
    {
        if (request.BusinessDate is null)
        {
            throw new ImportRunDateValidationException(
                "businessDate is required when period is not supplied.");
        }

        var businessDate = request.BusinessDate.Value;

        return new ImportRunDateResolution(
            Period: null,
            BusinessDate: businessDate,
            From: businessDate.ToDateTime(TimeOnly.MinValue),
            To: businessDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    private static ImportRunDateResolution ResolveCustom(ImportRunRequest request)
    {
        if (request.FromDate is null || request.ToDate is null)
        {
            throw new ImportRunDateValidationException(
                "fromDate and toDate are required when period is custom.");
        }

        if (request.ToDate.Value < request.FromDate.Value)
        {
            throw new ImportRunDateValidationException(
                "toDate must be greater than or equal to fromDate.");
        }

        return new ImportRunDateResolution(
            Period: "custom",
            BusinessDate: request.FromDate.Value,
            From: request.FromDate.Value.ToDateTime(TimeOnly.MinValue),
            To: request.ToDate.Value.ToDateTime(TimeOnly.MinValue));
    }

    private static DateTime ResolveTodayTo(DateTime now, string todayToMode)
    {
        if (string.IsNullOrWhiteSpace(todayToMode) ||
            todayToMode.Equals("CurrentHour", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        }

        if (todayToMode.Equals("CurrentTime", StringComparison.OrdinalIgnoreCase))
        {
            return now;
        }

        throw new ImportRunDateValidationException(
            "todayToMode must be one of: CurrentHour, CurrentTime.");
    }

    private static TimeZoneInfo ResolveTimeZone(string reportTimeZone)
    {
        if (string.IsNullOrWhiteSpace(reportTimeZone))
        {
            return TimeZoneInfo.Utc;
        }

        if (reportTimeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            reportTimeZone.Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(reportTimeZone);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ImportRunDateValidationException(
                $"reportTimeZone '{reportTimeZone}' was not found.",
                ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new ImportRunDateValidationException(
                $"reportTimeZone '{reportTimeZone}' is invalid.",
                ex);
        }
    }
}

public sealed record ImportRunDateResolution(
    string? Period,
    DateOnly BusinessDate,
    DateTime From,
    DateTime To);

public sealed class ImportRunDateValidationException : Exception
{
    public ImportRunDateValidationException(string message)
        : base(message)
    {
    }

    public ImportRunDateValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
