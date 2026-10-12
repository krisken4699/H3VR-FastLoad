using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>Everything known about one asset bundle load.</summary>
    public sealed class BundleRecord
    {
        public string Path;
        public string Name;
        /// <summary>Folder under BepInEx/plugins the file sits in.</summary>
        public string Mod;
        /// <summary>Assembly that asked for the load (usually a loader mod, not the content mod).</summary>
        public string By;
        public long Size;
        public bool Async;
        public int StartMs;
        public int EndMs = -1;
        /// <summary>Milliseconds the main thread spent inside the load call itself.</summary>
        public double MainMs;
        public long CommitStart;
        public long CommitEnd;
        public bool Ok;
        public int AssetCalls;
        public int LastUseMs = -1;
        public int UnloadMs = -1;

        public bool Done
        {
            get { return EndMs >= 0; }
        }

        /// <summary>Start to end in ms; -1 while still loading.</summary>
        public int WallMs
        {
            get { return EndMs < 0 ? -1 : EndMs - StartMs; }
        }

        /// <summary>Process commit growth while this bundle was loading. Loads that overlap share the same growth.</summary>
        public long CommitGrowth
        {
            get { return EndMs < 0 ? 0 : CommitEnd - CommitStart; }
        }

        public BundleRecord Copy()
        {
            return (BundleRecord)MemberwiseClone();
        }
    }

    /// <summary>The table of every bundle load seen this run. Safe from any thread.</summary>
    public static class BundleLedger
    {
        private static readonly object s_gate = new object();
        private static readonly List<BundleRecord> s_all = new List<BundleRecord>(1024);
        private static readonly Dictionary<int, BundleRecord> s_byId = new Dictionary<int, BundleRecord>();
        private static readonly List<BundleRecord> s_flying = new List<BundleRecord>();
        private static int s_loaded;

        public static int InFlight
        {
            get { return s_flying.Count; }
        }

        public static int LoadedCount
        {
            get { return s_loaded; }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_all.Clear();
                s_byId.Clear();
                s_flying.Clear();
                s_loaded = 0;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static BundleRecord Begin(string path, long size, string by, bool isAsync, int nowMs, long commit)
        {
            BundleRecord r = new BundleRecord();
            r.Path = path;
            r.Name = NameOf(path);
            r.Mod = ModOf(path);
            r.By = by ?? "?";
            r.Size = size;
            r.Async = isAsync;
            r.StartMs = nowMs;
            r.CommitStart = commit;
            Monitor.Enter(s_gate);
            try
            {
                s_all.Add(r);
                s_flying.Add(r);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
            return r;
        }

        /// <param name="bundleId">Unity instance id of the loaded bundle; 0 when the load gave nothing.</param>
        public static void End(BundleRecord r, int bundleId, bool ok, int nowMs, long commit, double mainMs)
        {
            if (r == null) return;
            Monitor.Enter(s_gate);
            try
            {
                if (r.EndMs >= 0) return;
                r.EndMs = nowMs;
                r.CommitEnd = commit;
                r.MainMs += mainMs;
                r.Ok = ok;
                s_flying.Remove(r);
                if (ok)
                {
                    s_loaded++;
                    if (bundleId != 0) s_byId[bundleId] = r;
                }
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Counts one asset call on a known bundle. Null when the id is not known.</summary>
        public static BundleRecord Use(int bundleId, int nowMs)
        {
            if (bundleId == 0) return null;
            Monitor.Enter(s_gate);
            try
            {
                BundleRecord r;
                if (!s_byId.TryGetValue(bundleId, out r)) return null;
                r.AssetCalls++;
                r.LastUseMs = nowMs;
                return r;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Makes a record for a bundle that was loaded where the hooks could not see, and counts this call.</summary>
        public static BundleRecord Adopt(int bundleId, string name, int nowMs)
        {
            BundleRecord r = new BundleRecord();
            r.Name = name ?? "?";
            r.Mod = "(not seen loading)";
            r.By = "?";
            r.StartMs = nowMs;
            r.EndMs = nowMs;
            r.Ok = true;
            r.AssetCalls = 1;
            r.LastUseMs = nowMs;
            Monitor.Enter(s_gate);
            try
            {
                s_all.Add(r);
                s_loaded++;
                if (bundleId != 0) s_byId[bundleId] = r;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
            return r;
        }

        public static void Unloaded(BundleRecord r, int nowMs)
        {
            if (r == null) return;
            Monitor.Enter(s_gate);
            try
            {
                if (r.UnloadMs >= 0) return;
                r.UnloadMs = nowMs;
                if (r.Ok) s_loaded--;
                int key = 0;
                foreach (KeyValuePair<int, BundleRecord> kv in s_byId)
                {
                    if (ReferenceEquals(kv.Value, r))
                    {
                        key = kv.Key;
                        break;
                    }
                }
                if (key != 0) s_byId.Remove(key);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>"a, b, c (+3)": names of bundles loading right now, at most <paramref name="max"/>.</summary>
        public static string InFlightNames(int max)
        {
            Monitor.Enter(s_gate);
            try
            {
                int n = s_flying.Count;
                if (n == 0) return "";
                int show = Math.Min(n, max);
                string[] names = new string[show];
                for (int i = 0; i < show; i++) names[i] = s_flying[i].Name;
                string joined = string.Join(", ", names);
                return n > show ? joined + " (+" + (n - show) + ")" : joined;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Copies of every record, in the order the loads began.</summary>
        public static List<BundleRecord> Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                List<BundleRecord> copy = new List<BundleRecord>(s_all.Count);
                for (int i = 0; i < s_all.Count; i++) copy.Add(s_all[i].Copy());
                return copy;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>The folder right under ".../plugins/" in the path; "(memory)" with no path; "(game)" elsewhere.</summary>
        public static string ModOf(string path)
        {
            if (path == null) return "(memory)";
            string p = path.Replace('/', '\\');
            int at = p.IndexOf("\\plugins\\", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "(game)";
            int from = at + "\\plugins\\".Length;
            int to = p.IndexOf('\\', from);
            if (to < 0) return "(plugins root)";
            return p.Substring(from, to - from);
        }

        public static string NameOf(string path)
        {
            if (path == null) return "(memory)";
            string p = path.Replace('/', '\\');
            int at = p.LastIndexOf('\\');
            return at < 0 ? p : p.Substring(at + 1);
        }
    }
}
