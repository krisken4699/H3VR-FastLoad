namespace FastLoad
{
    /// <summary>
    /// Finds "quiet": the first 10 s after the menu scene loaded with no bundle loading and no slow frame. That is
    /// the moment the menu is really usable.
    /// </summary>
    public sealed class QuietTracker
    {
        public const int WindowMs = 10000;

        private int m_since = -1;
        private bool m_done;

        /// <param name="menuAtMs">When the menu scene loaded; negative while it has not.</param>
        /// <param name="busy">A bundle is loading, or this frame was slow.</param>
        /// <returns>Once: the time the quiet window began. Otherwise -1.</returns>
        public int Step(int nowMs, int menuAtMs, bool busy)
        {
            if (m_done || menuAtMs < 0) return -1;
            if (busy || m_since < 0)
            {
                m_since = nowMs;
                return -1;
            }
            if (nowMs - m_since >= WindowMs)
            {
                m_done = true;
                return m_since;
            }
            return -1;
        }
    }
}
