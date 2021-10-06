using System;
using System.Collections.Generic;
using System.Text;

namespace Betty.Bot.Extensions
{
    public static class StringExtensions
    {
        public static string TrimToMax(this string str, int max, string denote = "...", bool removeWhitespaceNewlines = false)
        {
            if (str.Length > max)
                str = str.Substring(0, max - denote.Length) + denote;

            if (removeWhitespaceNewlines)
                str = str.Replace("\r", "").Replace("\n", " ");

            return str;
        }
    }
}
