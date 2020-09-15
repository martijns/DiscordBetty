using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Betty.Bot.Extensions
{
    public static class TimeSpanExtensions
    {
        private enum TimeSpanElement
        {
            millisecond,
            second,
            minute,
            hour,
            day
        }

        public static string ToFriendlyDisplay(this TimeSpan timeSpan, int maxNrOfElements)
        {
            maxNrOfElements = Math.Max(Math.Min(maxNrOfElements, 5), 1);
            var parts = new[]
            {
                Tuple.Create(TimeSpanElement.day, timeSpan.Days),
                Tuple.Create(TimeSpanElement.hour, timeSpan.Hours),
                Tuple.Create(TimeSpanElement.minute, timeSpan.Minutes),
                Tuple.Create(TimeSpanElement.second, timeSpan.Seconds),
                Tuple.Create(TimeSpanElement.millisecond, timeSpan.Milliseconds)
            }
            .SkipWhile(i => i.Item2 <= 0)
            .Take(maxNrOfElements);

            return string.Join(", ", parts.Select(p => string.Format("{0} {1}{2}", p.Item2, p.Item1, p.Item2 > 1 || p.Item2 == 0 ? "s" : string.Empty)));
        }
    }
}
