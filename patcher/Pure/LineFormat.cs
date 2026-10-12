using System;
using System.Globalization;

namespace FastLoad.Early
{
    /// <summary>How timeline lines and numbers are written. Always the same on every PC locale.</summary>
    public static class LineFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"   48213 f=1432 LOG  text": ms since process start, Unity frame ("-" before the first), kind, text.</summary>
        public static string Line(int ms, int frame, string kind, string text)
        {
            return ms.ToString(Inv).PadLeft(8) + " f=" + (frame < 0 ? "-" : frame.ToString(Inv)) + " " + kind + "  " + text;
        }

        /// <summary>One event is one line: line breaks inside a message become " \n ".</summary>
        public static string Flatten(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\r\n", " \\n ").Replace("\n", " \\n ").Replace("\r", " \\n ");
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576L).ToString(Inv) + "MB";
        }

        public static string SignedMb(long bytes)
        {
            long mb = bytes / 1048576L;
            return (mb >= 0 ? "+" : "") + mb.ToString(Inv) + "MB";
        }

        public static string N0(double v)
        {
            return ((long)Math.Round(v)).ToString(Inv);
        }

        public static string N1(double v)
        {
            return v.ToString("0.0", Inv);
        }

        /// <summary>Milliseconds as seconds, or "not seen" for a mark that never came (negative).</summary>
        public static string Sec(int ms)
        {
            return ms < 0 ? "not seen" : (ms / 1000.0).ToString("0.0", Inv) + " s";
        }
    }
}
