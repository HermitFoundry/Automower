namespace AutomowerConsole
{
    internal static class Extensions
    {
        extension(DateTimeOffset now)
        {
            /// <summary>
            /// Nighttime window wraps past midnight when startHour > endHour (e.g. 22 -> 8).
            /// </summary>
            public bool IsNighttime(int startHour, int endHour)
                => startHour > endHour
                    ? now.Hour >= startHour || now.Hour < endHour
                    : now.Hour >= startHour && now.Hour < endHour;
        }

        extension(TimeSpan span)
        {
            public string FormatDuration()
            {
                var totalMinutes = Math.Max(0, (int)span.TotalMinutes);
                var hours = totalMinutes / 60;
                var minutes = totalMinutes % 60;
                return hours > 0 ? $"{hours}h{minutes:D2}m" : $"{minutes}m";
            }
        }
    }
}
