using TimeLedger.Domain.Entities;

namespace TimeLedger.Domain.Rules;

public static class LeaveCalendar
{
    /// <summary>Number of working days a leave request consumes.</summary>
    public static decimal CountDays(DateOnly start, DateOnly end, WorkSchedule schedule, IReadOnlySet<DateOnly> holidays) =>
        SchedulePolicy.WorkingDays(start, end, schedule, holidays).Count();

    /// <summary>Working days per calendar year, because each year has its own annual balance.</summary>
    public static IReadOnlyDictionary<int, decimal> CountDaysByYear(
        DateOnly start, DateOnly end, WorkSchedule schedule, IReadOnlySet<DateOnly> holidays) =>
        SchedulePolicy.WorkingDays(start, end, schedule, holidays)
            .GroupBy(d => d.Year)
            .ToDictionary(g => g.Key, g => (decimal)g.Count());

    /// <summary>Expands approved requests into a per-day map, counting working days only.</summary>
    public static IReadOnlyDictionary<DateOnly, LeaveType> ExpandApproved(
        IEnumerable<LeaveRequest> requests, WorkSchedule schedule, IReadOnlySet<DateOnly> holidays)
    {
        var map = new Dictionary<DateOnly, LeaveType>();
        foreach (var request in requests.Where(r => r.Status == LeaveStatus.Approved))
        {
            foreach (var day in SchedulePolicy.WorkingDays(request.StartDate, request.EndDate, schedule, holidays))
            {
                map[day] = request.Type;
            }
        }

        return map;
    }
}
