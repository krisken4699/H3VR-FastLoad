using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>
    /// What is loading right now, for the loading window: who feeds it (bundle hooks, the ModulWorkshop_Optimizer
    /// bridge) says Begin and End; the window asks for one line of text.
    ///
    /// Loads that follow each other with less than BurstGapMs between them are one batch, counted "5/24".
    /// </summary>
    public static class LoadFeed
    {
        public const int BurstGapMs = 1500;

        private sealed class Task
        {
            public int Token;
            public string What;
            public string Mod;
            public bool Urgent;
        }

        private static readonly object s_gate = new object();
        private static readonly List<Task> s_active = new List<Task>();
        private static int s_nextToken = 1;
        private static int s_total;
        private static int s_done;
        /// <summary>Loads announced by Expect that have not begun yet.</summary>
        private static int s_credit;
        private static int s_idleSinceMs = -1;
        private static int s_burstStartMs;
        private static int s_urgent;

        /// <summary>Loads in flight that someone is waiting for (a gun that cannot appear before they are done).</summary>
        public static int UrgentActive
        {
            get { return s_urgent; }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_active.Clear();
                s_total = 0;
                s_done = 0;
                s_credit = 0;
                s_idleSinceMs = -1;
                s_urgent = 0;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>A new batch starts when nothing is loading and the last load ended long enough ago.</summary>
        private static void Fresh(int nowMs)
        {
            if (s_active.Count > 0 || s_credit > 0) return;
            if (s_total > 0 && s_idleSinceMs >= 0 && nowMs - s_idleSinceMs < BurstGapMs) return;
            s_total = 0;
            s_done = 0;
            s_burstStartMs = nowMs;
        }

        /// <summary>This many loads are about to begin, one after another: the count can show the real total from the start.</summary>
        public static void Expect(int count, int nowMs)
        {
            if (count <= 0) return;
            Monitor.Enter(s_gate);
            try
            {
                Fresh(nowMs);
                s_total += count;
                s_credit += count;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <param name="announced">True when this load was counted by Expect already.</param>
        /// <returns>A token for End.</returns>
        public static int Begin(string what, string mod, bool urgent, bool announced, int nowMs)
        {
            Monitor.Enter(s_gate);
            try
            {
                Fresh(nowMs);
                if (announced && s_credit > 0) s_credit--;
                else s_total++;
                Task t = new Task();
                t.Token = s_nextToken++;
                t.What = what ?? "";
                t.Mod = mod ?? "";
                t.Urgent = urgent;
                s_active.Add(t);
                if (urgent) s_urgent++;
                return t.Token;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static void End(int token, int nowMs)
        {
            if (token <= 0) return;
            Monitor.Enter(s_gate);
            try
            {
                for (int i = 0; i < s_active.Count; i++)
                {
                    if (s_active[i].Token != token) continue;
                    if (s_active[i].Urgent) s_urgent--;
                    s_active.RemoveAt(i);
                    s_done++;
                    if (s_active.Count == 0) s_idleSinceMs = nowMs;
                    return;
                }
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>
        /// The window's text, or false when there is nothing to show: nothing loading, or the batch is younger
        /// than minAgeMs (a load that is over in a blink shows nothing).
        /// </summary>
        public static bool Line(int nowMs, int minAgeMs, out string text)
        {
            text = "";
            Monitor.Enter(s_gate);
            try
            {
                if (s_active.Count == 0) return false;
                if (nowMs - s_burstStartMs < minAgeMs) return false;
                Task t = s_active[0];
                text = "Loading " + t.What;
                // "(game)", "(memory)": not a mod.
                if (t.Mod.Length > 0 && t.Mod[0] != '(') text += " from " + t.Mod;
                if (s_total > 1) text += "   " + Math.Min(s_done + 1, s_total) + "/" + s_total;
                return true;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }
    }
}
