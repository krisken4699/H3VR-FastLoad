using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FastLoad.Early
{
    /// <summary>
    /// Harmony patches on UnityEngine.AssetBundle that feed the ledger. They only observe: no argument or result is
    /// ever changed. All of this runs on Unity's main thread (bundle calls are main-thread only).
    /// </summary>
    public static class BundleHooks
    {
        /// <summary>What a prefix hands to its postfix.</summary>
        public struct Call
        {
            public BundleRecord Rec;
            public long T0;
        }

        private struct Pending
        {
            public BundleRecord Rec;
            public AssetBundleCreateRequest Req;
        }

        private struct Live
        {
            public BundleRecord Rec;
            public AssetBundle Bundle;
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>A single asset call that holds the main thread this long gets its own timeline line.</summary>
        private const double SlowAssetMs = 5.0;

        private static readonly List<Pending> s_pending = new List<Pending>();
        private static readonly List<Live> s_live = new List<Live>();
        /// <summary>Records a load prefix opened whose postfix has not run yet.</summary>
        private static readonly List<BundleRecord> s_opened = new List<BundleRecord>();
        private static bool s_installed;
        private static bool s_loadsOff;
        private static bool s_assetsOff;

        public static string Mode = "not installed";

        public static void Install(bool native)
        {
            if (s_installed) return;
            s_installed = true;
            try
            {
                Harmony h = new Harmony("invent60.h3vr.fastload.early");
                Type ab = typeof(AssetBundle);
                Type str = typeof(string);
                Type u32 = typeof(uint);
                Type u64 = typeof(ulong);
                Type bytes = typeof(byte[]);
                Type type = typeof(Type);
                int on = 0;
                int all = 0;

                if (native)
                {
                    all += 4;
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str, u32, u64 }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str, u32, u64 }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromMemory", new Type[] { bytes, u32 }, "MemPre", "SyncPost");
                    on += Patch(h, ab, "LoadFromMemoryAsync", new Type[] { bytes, u32 }, "MemAsyncPre", "AsyncPost");
                }
                else
                {
                    all += 6;
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str, u32 }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str, u32 }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromMemory", new Type[] { bytes }, "MemPre", "SyncPost");
                    on += Patch(h, ab, "LoadFromMemoryAsync", new Type[] { bytes }, "MemAsyncPre", "AsyncPost");
                }

                all += 6;
                on += Patch(h, ab, "LoadAsset", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetAsync", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetWithSubAssets", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetWithSubAssetsAsync", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAllAssets", new Type[] { type }, "AssetPre", "AllAssetsPost");
                on += Patch(h, ab, "LoadAllAssetsAsync", new Type[] { type }, "AssetPre", "AllAssetsPost");

                Mode = (native ? "native" : "managed") + ", " + on + " of " + all + " hooks on";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            Timeline.Write("HKS", Mode);
        }

        private static int Patch(Harmony h, Type target, string name, Type[] args, string prefix, string postfix)
        {
            try
            {
                MethodInfo m = target.GetMethod(name, Any, null, args, null);
                if (ReferenceEquals(m, null))
                {
                    Timeline.Write("ERR", "hook target not found: AssetBundle." + name + "/" + args.Length);
                    return 0;
                }
                Type me = typeof(BundleHooks);
                h.Patch(m, new HarmonyMethod(me.GetMethod(prefix, Any)), new HarmonyMethod(me.GetMethod(postfix, Any)));
                return 1;
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "hook not applied: AssetBundle." + name + "/" + args.Length + " (" + e.GetType().Name + ": " + e.Message + ")");
                return 0;
            }
        }

        // ---- bundle loads ------------------------------------------------------------------------------------

        // The methods Harmony calls are thin shells: whatever goes wrong behind them, even the runtime failing to
        // compile the method behind, stays here and never reaches the game's or another mod's call.

        private static void FilePre(string path, out Call __state)
        {
            __state = new Call();
            try { __state = Open(path, -1, false); } catch (Exception) { }
        }

        private static void FileAsyncPre(string path, out Call __state)
        {
            __state = new Call();
            try { __state = Open(path, -1, true); } catch (Exception) { }
        }

        private static void MemPre(byte[] binary, out Call __state)
        {
            __state = new Call();
            try { __state = Open(null, binary != null ? binary.Length : 0, false); } catch (Exception) { }
        }

        private static void MemAsyncPre(byte[] binary, out Call __state)
        {
            __state = new Call();
            try { __state = Open(null, binary != null ? binary.Length : 0, true); } catch (Exception) { }
        }

        private static Call Open(string path, long memorySize, bool isAsync)
        {
            Call c = new Call();
            if (s_loadsOff) return c;
            try
            {
                SweepPending();
                long size = memorySize;
                if (path != null)
                {
                    size = 0;
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists) size = fi.Length;
                    }
                    catch (Exception)
                    {
                    }
                }
                c.Rec = BundleLedger.Begin(path, size, Caller(), isAsync, Clock.NowMs, Memory.Commit());
                s_opened.Add(c.Rec);
                Timeline.Write("BUN", "load-start name=" + c.Rec.Name + " mod=" + c.Rec.Mod + " size=" + LineFormat.Mb(size)
                    + " by=" + c.Rec.By + " async=" + (isAsync ? "1" : "0") + " path=" + (path ?? "(memory)"));
                c.T0 = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                LoadsFailed("load prefix", e);
                c.Rec = null;
            }
            return c;
        }

        private static void SyncPost(AssetBundle __result, Call __state)
        {
            try { SyncDone(__result, __state); } catch (Exception) { }
        }

        private static void AsyncPost(AssetBundleCreateRequest __result, Call __state)
        {
            try { AsyncStarted(__result, __state); } catch (Exception) { }
        }

        private static void SyncDone(AssetBundle __result, Call __state)
        {
            if (__state.Rec == null) return;
            try
            {
                s_opened.Remove(__state.Rec);
                double main = Elapsed(__state.T0);
                bool ok = __result != null;
                Close(__state.Rec, ok ? __result : null, main);
            }
            catch (Exception e)
            {
                LoadsFailed("load postfix", e);
            }
        }

        private static void AsyncStarted(AssetBundleCreateRequest __result, Call __state)
        {
            if (__state.Rec == null) return;
            try
            {
                s_opened.Remove(__state.Rec);
                __state.Rec.MainMs += Elapsed(__state.T0);
                if (__result == null)
                {
                    Close(__state.Rec, null, 0);
                    return;
                }
                Pending p = new Pending();
                p.Rec = __state.Rec;
                p.Req = __result;
                s_pending.Add(p);
            }
            catch (Exception e)
            {
                LoadsFailed("async load postfix", e);
            }
        }

        private static void Close(BundleRecord rec, AssetBundle bundle, double mainMs)
        {
            bool ok = bundle != null;
            BundleLedger.End(rec, ok ? bundle.GetInstanceID() : 0, ok, Clock.NowMs, Memory.Commit(), mainMs);
            if (ok)
            {
                Live l = new Live();
                l.Rec = rec;
                l.Bundle = bundle;
                s_live.Add(l);
            }
            Timeline.Write("BUN", "load-end name=" + rec.Name + " mod=" + rec.Mod + " wall=" + rec.WallMs + "ms main=" + LineFormat.N0(rec.MainMs)
                + "ms commit=" + LineFormat.SignedMb(rec.CommitGrowth) + " ok=" + (ok ? "1" : "0"));
        }

        /// <summary>
        /// Once per frame, from the sampler: closes finished async loads, and closes as failed any load whose call
        /// never came back to its postfix (the engine call threw). Between two frames no load call is in progress,
        /// so anything a prefix opened and no postfix took is an orphan.
        /// </summary>
        public static void Sweep()
        {
            SweepPending();
            if (s_opened.Count == 0) return;
            try
            {
                for (int i = 0; i < s_opened.Count; i++)
                {
                    BundleRecord r = s_opened[i];
                    BundleLedger.Fail(r, Clock.NowMs, Memory.Commit());
                    Timeline.Write("BUN", "load-end name=" + r.Name + " mod=" + r.Mod + " ok=0 (the load call threw)");
                }
            }
            catch (Exception)
            {
            }
            s_opened.Clear();
        }

        /// <summary>Closes async loads that have finished. Cheap when nothing is pending.</summary>
        private static void SweepPending()
        {
            if (s_pending.Count == 0) return;
            try
            {
                for (int i = s_pending.Count - 1; i >= 0; i--)
                {
                    Pending p = s_pending[i];
                    // assetBundle on a request that is not done would force the load to finish right now: only after isDone.
                    if (!p.Req.isDone) continue;
                    s_pending.RemoveAt(i);
                    Close(p.Rec, p.Req.assetBundle, 0);
                }
            }
            catch (Exception e)
            {
                s_pending.Clear();
                LoadsFailed("sweep", e);
            }
        }

        /// <summary>
        /// Unload is an engine-native method and is not hooked. A bundle Unity reports as destroyed is marked unloaded
        /// here; called once per second, so the time is good to about a second.
        /// </summary>
        public static void CheckUnloads()
        {
            if (s_live.Count == 0) return;
            try
            {
                for (int i = s_live.Count - 1; i >= 0; i--)
                {
                    Live l = s_live[i];
                    if (l.Bundle != null) continue;
                    s_live.RemoveAt(i);
                    BundleLedger.Unloaded(l.Rec, Clock.NowMs);
                    Timeline.Write("BUN", "unload name=" + l.Rec.Name + " mod=" + l.Rec.Mod + " assetCalls=" + l.Rec.AssetCalls);
                }
            }
            catch (Exception e)
            {
                s_live.Clear();
                LoadsFailed("unload check", e);
            }
        }

        // ---- asset use ---------------------------------------------------------------------------------------

        private static void AssetPre(out long __state)
        {
            __state = s_assetsOff ? 0 : Stopwatch.GetTimestamp();
        }

        private static void NamedAssetPost(AssetBundle __instance, string name, long __state)
        {
            try { Used(__instance, name, __state); } catch (Exception) { }
        }

        private static void AllAssetsPost(AssetBundle __instance, long __state)
        {
            try { Used(__instance, "(all)", __state); } catch (Exception) { }
        }

        private static void Used(AssetBundle bundle, string asset, long t0)
        {
            if (s_assetsOff || t0 == 0) return;
            try
            {
                double ms = Elapsed(t0);
                int now = Clock.NowMs;
                int id = bundle.GetInstanceID();
                BundleRecord r = BundleLedger.Use(id, now);
                if (r == null && s_pending.Count > 0)
                {
                    // An async load is only tied to its bundle when it is closed. A caller that uses the bundle the
                    // moment the load finishes (or forces it to finish) gets here first: close finished loads, look again.
                    SweepPending();
                    r = BundleLedger.Use(id, now);
                }
                if (r == null) r = BundleLedger.Adopt(id, bundle.name, now);
                if (ms >= SlowAssetMs)
                    Timeline.Write("BUN", "asset bundle=" + r.Name + " mod=" + r.Mod + " asset=" + asset + " main=" + LineFormat.N0(ms) + "ms");
            }
            catch (Exception e)
            {
                s_assetsOff = true;
                Timeline.Write("ERR", "asset hooks switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static double Elapsed(long t0)
        {
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        private static void LoadsFailed(string where, Exception e)
        {
            if (s_loadsOff) return;
            s_loadsOff = true;
            Timeline.Write("ERR", "bundle load hooks switched off (" + where + "): " + e.GetType().Name + ": " + e.Message);
            try
            {
                s_pending.Clear();
                s_opened.Clear();
                int closed = BundleLedger.FailAllFlying(Clock.NowMs, Memory.Commit());
                if (closed > 0) Timeline.Write("ERR", closed + " load(s) that were in flight are recorded as failed");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The first assembly on the call stack that is not the engine, a patching library or this mod.</summary>
        private static string Caller()
        {
            try
            {
                StackTrace st = new StackTrace(2, false);
                int n = st.FrameCount;
                for (int i = 0; i < n; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (ReferenceEquals(m, null)) continue;
                    Type t = m.DeclaringType;
                    if (ReferenceEquals(t, null)) continue;    // Harmony's generated wrapper has no declaring type
                    string a = t.Assembly.GetName().Name;
                    if (a == "UnityEngine" || a == "mscorlib" || a == "0Harmony" || a == "FastLoadPatcher" || a == "FastLoad") continue;
                    if (a.StartsWith("MonoMod", StringComparison.Ordinal) || a.StartsWith("System", StringComparison.Ordinal)) continue;
                    return a;
                }
            }
            catch (Exception)
            {
            }
            return "?";
        }
    }
}
