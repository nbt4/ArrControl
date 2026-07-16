using ArrControl.Application.Automation;
using Cronos;

namespace ArrControl.Infrastructure.Automation;

public sealed class CronosScheduleCalculator : ICronScheduleCalculator
{
    public DateTimeOffset? GetNextOccurrence(
        string expression,
        string timeZone,
        DateTimeOffset after)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 160)
        {
            throw new ScheduledJobException("schedule_cron_invalid");
        }

        try
        {
            var format = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
                ? CronFormat.IncludeSeconds
                : CronFormat.Standard;
            var cron = CronExpression.Parse(expression, format);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return cron.GetNextOccurrence(after, zone, inclusive: false);
        }
        catch (Exception exception) when (
            exception is CronFormatException
                or TimeZoneNotFoundException
                or InvalidTimeZoneException)
        {
            throw new ScheduledJobException("schedule_cron_invalid");
        }
    }
}
