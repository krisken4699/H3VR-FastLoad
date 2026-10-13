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
            public bool Alone;
        }

        private static readonly object s_gate = new object();
        private static readonly List<Task> s_active = new List<Task>();
        private static readonly List<Task> s_alone = new List<Task>();
        private static int s_nextToken = 1;
        private static int s_total;
        private static int s_done;
        /// <summary>Loads announced by Expect that have not begun yet.</summary>
        private static int s_credit;
        private static int s_idleSinceMs = -1;
        private static int s_burstStartMs;
        private static int s_urgent;
        // The same batch counting, for waited-for loads only.
        private static int s_uTotal;
        private static int s_uDone;
        private static int s_uIdleSinceMs = -1;

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
                s_alone.Clear();
                s_total = 0;
                s_done = 0;
                s_credit = 0;
                s_idleSinceMs = -1;
                s_urgent = 0;
                s_uTotal = 0;
                s_uDone = 0;
                s_uIdleSinceMs = -1;
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

        private static void UrgentFresh(int nowMs, int add)
        {
            if (s_urgent == 0 && (s_uIdleSinceMs < 0 || nowMs - s_uIdleSinceMs >= BurstGapMs) && s_credit <= add)
            {
                s_uTotal = 0;
                s_uDone = 0;
            }
            s_uTotal += add;
        }

        /// <summary>
        /// The line for a player who wants a clean view: the map on its way, and loads something is waiting for (the
        /// part packs of a gun being spawned). Background loading, like the item lists after game start, is left out.
        /// </summary>
        public static bool WaitedLine(int nowMs, out string text)
        {
            text = "";
            Monitor.Enter(s_gate);
            try
            {
                if (s_alone.Count > 0)
                {
                    text = "Loading " + s_alone[0].What;
                    return true;
                }
                for (int i = 0; i < s_active.Count; i++)
                {
                    Task t = s_active[i];
                    if (!t.Urgent) continue;
                    text = "Loading " + t.What;
                    if (t.Mod.Length > 0 && t.Mod[0] != '(') text += " from " + t.Mod;
                    if (s_uTotal > 1) text += "   " + Math.Min(s_uDone + 1, s_uTotal) + "/" + s_uTotal;
                    return true;
                }
                return false;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
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
                // Only waited-for loads are announced.
                UrgentFresh(nowMs, count);
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
                else
                {
                    s_total++;
                    if (urgent) UrgentFresh(nowMs, 1);
                }
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

        /// <summary>
        /// A load that is shown by itself, before anything else that is loading and without a count: the map on its
        /// way while hundreds of item bundles load behind it.
        /// </summary>
        public static int BeginAlone(string what, int nowMs)
        {
            Monitor.Enter(s_gate);
            try
            {
                Task t = new Task();
                t.Token = s_nextToken++;
                t.What = what ?? "";
                t.Mod = "";
                t.Alone = true;
                s_alone.Add(t);
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
                for (int i = 0; i < s_alone.Count; i++)
                {
                    if (s_alone[i].Token != token) continue;
                    s_alone.RemoveAt(i);
                    return;
                }
                for (int i = 0; i < s_active.Count; i++)
                {
                    if (s_active[i].Token != token) continue;
                    if (s_active[i].Urgent)
                    {
                        s_urgent--;
                        s_uDone++;
                        if (s_urgent == 0) s_uIdleSinceMs = nowMs;
                    }
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
                if (s_alone.Count > 0)
                {
                    text = "Loading " + s_alone[0].What;
                    return true;
                }
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
