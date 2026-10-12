using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using FastLoad.Early;
using HarmonyLib;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Test tool: which mods cost main-thread time every frame, just by being loaded.
    ///
    /// Times two kinds of mod code: the per-frame methods of every mod component (Update, LateUpdate, FixedUpdate,
    /// OnGUI and the render callbacks), and every Harmony patch method a mod has put on someone else's method
    /// (prefixes, postfixes, finalizers). Not seen: coroutines, transpilers, and what a mod makes the game itself
    /// do more of (more objects to draw, more physics).
    ///
    /// Only ever switched on by an autotest step: it patches thousands of methods and stays in until the game closes.
    /// </summary>
    public static class LagProfiler
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly string[] FrameMethods = new string[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnPreRender", "OnPostRender",
            "OnRenderObject", "OnWillRenderObject", "OnPreCull", "OnRenderImage" };

        private sealed class Cost
        {
            public long Ticks;
            public long Calls;
            public string Name;
            public string Mod;
            public string Kind;
        }

        private static readonly Dictionary<MethodBase, Cost> s_costs = new Dictionary<MethodBase, Cost>();
        private static bool s_installed;
        private static bool s_measuring;
        private static int s_patched;
        private static int s_failed;
        private static int s_frame0;
        private static readonly Stopwatch s_window = new Stopwatch();

        public static string Install()
        {
            if (s_installed) return "already in: " + s_patched + " methods";
            s_installed = true;
            Stopwatch sw = Stopwatch.StartNew();
            Harmony h = new Harmony("ken.h3vr.fastload.lagprofiler");
            HarmonyMethod pre = new HarmonyMethod(typeof(LagProfiler).GetMethod("Pre", Any));
            HarmonyMethod post = new HarmonyMethod(typeof(LagProfiler).GetMethod("Post", Any));
            Assembly mine = typeof(LagProfiler).Assembly;
            Assembly early = typeof(Timeline).Assembly;
            string plugins = Paths.PluginPath;

            // 1. Harmony patch methods mods put on other code. Listed first, before our own patches join the lists.
            List<KeyValuePair<MethodInfo, string>> patchMethods = new List<KeyValuePair<MethodInfo, string>>();
            Dictionary<MethodBase, bool> seen = new Dictionary<MethodBase, bool>();
            foreach (MethodBase target in Harmony.GetAllPatchedMethods())
            {
                Patches info;
                try
                {
                    info = Harmony.GetPatchInfo(target);
                }
                catch (Exception)
                {
                    continue;
                }
                if (info == null) continue;
                string on = (ReferenceEquals(target.DeclaringType, null) ? "?" : target.DeclaringType.Name) + "." + target.Name;
                Collect(info.Prefixes, "prefix on " + on, patchMethods, seen, mine, early);
                Collect(info.Postfixes, "postfix on " + on, patchMethods, seen, mine, early);
                Collect(info.Finalizers, "finalizer on " + on, patchMethods, seen, mine, early);
            }
            for (int i = 0; i < patchMethods.Count; i++) Patch(h, patchMethods[i].Key, pre, post, patchMethods[i].Value);

            // 2. Per-frame methods of mod components.
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(a, mine) || ReferenceEquals(a, early)) continue;
                string where;
                try
                {
                    where = a.Location;
                }
                catch (Exception)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(where) || !where.StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) continue;
                Type[] types;
                try
                {
                    types = a.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch (Exception)
                {
                    continue;
                }
                for (int t = 0; types != null && t < types.Length; t++)
                {
                    Type type = types[t];
                    if (ReferenceEquals(type, null) || type.IsGenericTypeDefinition || type.IsAbstract && type.IsSealed) continue;
                    bool behaviour;
                    try
                    {
                        behaviour = typeof(MonoBehaviour).IsAssignableFrom(type);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!behaviour) continue;
                    for (int m = 0; m < FrameMethods.Length; m++)
                    {
                        MethodInfo method = null;
                        try
                        {
                            method = type.GetMethod(FrameMethods[m], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        }
                        catch (Exception)
                        {
                        }
                        if (ReferenceEquals(method, null) || method.IsAbstract || method.IsGenericMethodDefinition || seen.ContainsKey(method)) continue;
                        seen[method] = true;
                        Patch(h, method, pre, post, FrameMethods[m]);
                    }
                }
            }
            return "timing " + s_patched + " mod methods (" + patchMethods.Count + " Harmony patch methods), " + s_failed + " could not be timed, set up in "
                + (int)sw.Elapsed.TotalMilliseconds + " ms";
        }

        private static void Collect(IList<Patch> list, string kind, List<KeyValuePair<MethodInfo, string>> into, Dictionary<MethodBase, bool> seen, Assembly mine, Assembly early)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                MethodInfo m = list[i].PatchMethod;
                if (ReferenceEquals(m, null) || seen.ContainsKey(m) || ReferenceEquals(m.DeclaringType, null)) continue;
                Assembly a = m.DeclaringType.Assembly;
                if (ReferenceEquals(a, mine) || ReferenceEquals(a, early)) continue;
                // Dynamic methods have no body to patch.
                if (m is System.Reflection.Emit.DynamicMethod || m.IsGenericMethodDefinition || m.DeclaringType.IsGenericTypeDefinition) continue;
                seen[m] = true;
                into.Add(new KeyValuePair<MethodInfo, string>(m, kind));
            }
        }

        private static void Patch(Harmony h, MethodInfo method, HarmonyMethod pre, HarmonyMethod post, string kind)
        {
            try
            {
                Cost c = new Cost();
                c.Name = method.DeclaringType.FullName + "." + method.Name;
                c.Kind = kind;
                string file = "";
                try
                {
                    file = method.DeclaringType.Assembly.Location;
                }
                catch (Exception)
                {
                }
                c.Mod = file.Length > 0 ? BundleLedger.ModOf(file) : method.DeclaringType.Assembly.GetName().Name;
                if (c.Mod.Length == 0 || c.Mod[0] == '(') c.Mod = method.DeclaringType.Assembly.GetName().Name;
                h.Patch(method, pre, post);
                s_costs[method] = c;
                s_patched++;
            }
            catch (Exception)
            {
                s_failed++;
            }
        }

        private static void Pre(out long __state)
        {
            __state = s_measuring ? Stopwatch.GetTimestamp() : 0;
        }

        private static void Post(long __state, MethodBase __originalMethod)
        {
            if (__state == 0 || !s_measuring) return;
            long took = Stopwatch.GetTimestamp() - __state;
            Cost c;
            if (!s_costs.TryGetValue(__originalMethod, out c)) return;
            c.Ticks += took;
            c.Calls++;
        }

        public static void Begin()
        {
            foreach (Cost c in s_costs.Values)
            {
                c.Ticks = 0;
                c.Calls = 0;
            }
            s_frame0 = Time.frameCount;
            s_window.Reset();
            s_window.Start();
            s_measuring = true;
        }

        /// <summary>Stops timing and writes lag_*.txt; returns the one-line summary.</summary>
        public static string End(string label)
        {
            s_measuring = false;
            s_window.Stop();
            int frames = Math.Max(1, Time.frameCount - s_frame0);
            double seconds = s_window.Elapsed.TotalSeconds;
            double toMs = 1000.0 / Stopwatch.Frequency;

            Dictionary<string, double[]> byMod = new Dictionary<string, double[]>();
            List<Cost> methods = new List<Cost>();
            double total = 0;
            foreach (Cost c in s_costs.Values)
            {
                if (c.Calls == 0) continue;
                methods.Add(c);
                double ms = c.Ticks * toMs;
                total += ms;
                double[] row;
                if (!byMod.TryGetValue(c.Mod, out row)) byMod[c.Mod] = row = new double[2];
                row[0] += ms;
                row[1] += c.Calls;
            }
            List<KeyValuePair<string, double[]>> mods = new List<KeyValuePair<string, double[]>>(byMod);
            mods.Sort(delegate(KeyValuePair<string, double[]> a, KeyValuePair<string, double[]> b) { return b.Value[0].CompareTo(a.Value[0]); });
            methods.Sort(delegate(Cost a, Cost b) { return b.Ticks.CompareTo(a.Ticks); });

            StringBuilder sb = new StringBuilder();
            sb.Append("FastLoad lag profile '").Append(label).Append("': ").Append(frames).Append(" frames in ").Append(seconds.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" s (").Append((seconds * 1000.0 / frames).ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms a frame), scene ")
                .Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append('\n');
            sb.Append("Main-thread time inside mod code that runs every frame: ").Append(F(total / frames)).Append(" ms a frame over ").Append(mods.Count).Append(" mods.\n");
            sb.Append("Timed: mod components' per-frame methods and mods' Harmony prefixes/postfixes. Not timed: coroutines, transpilers, extra work the game does because of a mod.\n");
            sb.Append("A method that calls another timed method counts that time too (nested), so the mod sum can be more than what the frame really spent.\n\n");
            sb.Append("== By mod (ms a frame | calls a frame) ==\n");
            for (int i = 0; i < mods.Count && i < 60; i++)
                sb.Append(F(mods[i].Value[0] / frames).PadLeft(8)).Append(" ms | ").Append(F(mods[i].Value[1] / frames).PadLeft(8)).Append(" | ").Append(mods[i].Key).Append('\n');
            sb.Append("\n== By method (ms a frame | calls a frame | microseconds a call) ==\n");
            for (int i = 0; i < methods.Count && i < 80; i++)
            {
                Cost c = methods[i];
                double ms = c.Ticks * toMs;
                sb.Append(F(ms / frames).PadLeft(8)).Append(" ms | ").Append(F((double)c.Calls / frames).PadLeft(8)).Append(" | ").Append(F(ms * 1000.0 / c.Calls).PadLeft(8))
                    .Append(" us | ").Append(c.Mod).Append(" | ").Append(c.Name).Append(" [").Append(c.Kind).Append("]\n");
            }
            string path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "FastLoad"), "lag_" + Timeline.Stamp + "_" + label + ".txt");
            File.WriteAllText(path, sb.ToString());

            string top = "";
            for (int i = 0; i < mods.Count && i < 6; i++) top += (i == 0 ? "" : ", ") + mods[i].Key + " " + F(mods[i].Value[0] / frames) + "ms";
            return "mod code " + F(total / frames) + " ms of " + F(seconds * 1000.0 / frames) + " ms a frame -> " + Path.GetFileName(path) + " | top: " + top;
        }

        private static string F(double v)
        {
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
