using System.Diagnostics;

namespace FastLoad.Early
{
    /// <summary>The run clock: real milliseconds since the game process started. Never game time.</summary>
    public static class Clock
    {
        private static readonly Stopwatch s_watch = new Stopwatch();
        private static double s_offsetMs;

        /// <param name="msSinceProcessStart">How long the process had already been running when this is called.</param>
        public static void Start(double msSinceProcessStart)
        {
            s_offsetMs = msSinceProcessStart;
            s_watch.Reset();
            s_watch.Start();
        }

        public static double NowMsExact
        {
            get { return s_offsetMs + s_watch.Elapsed.TotalMilliseconds; }
        }

        public static int NowMs
        {
            get { return (int)NowMsExact; }
        }
    }
}
