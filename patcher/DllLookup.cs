using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Mono.Cecil;

namespace FastLoad.Early
{
    /// <summary>
    /// Speed-up: answers BepInEx's "find this assembly somewhere under this folder" from an index read once
    /// (DllIndex) instead of a walk over the whole folder tree per question. Same answers, same order.
    /// Every question is counted, and each name that was not found is written to the timeline once with who asked.
    /// </summary>
    public static class DllLookup
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private const int MaxNamed = 400;

        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, DllIndex> s_roots = new Dictionary<string, DllIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> s_missed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int s_calls;
        private static int s_found;
        private static double s_indexMs;
        private static bool s_broken;

        public static string Mode = "off";

        public static void Install()
        {
            try
            {
                Harmony h = new Harmony("ken.h3vr.fastload.dlllookup");
                Type util = typeof(BepInEx.Utility);
                MethodInfo asm = util.GetMethod("TryResolveDllAssembly", Any, null,
                    new Type[] { typeof(AssemblyName), typeof(string), typeof(Assembly).MakeByRefType() }, null);
                MethodInfo cecil = util.GetMethod("TryResolveDllAssembly", Any, null,
                    new Type[] { typeof(AssemblyName), typeof(string), typeof(ReaderParameters), typeof(AssemblyDefinition).MakeByRefType() }, null);
                int on = 0;
                if (!ReferenceEquals(asm, null))
                {
                    h.Patch(asm, new HarmonyMethod(typeof(DllLookup).GetMethod("AsmPre", Any)));
                    on++;
                }
                if (!ReferenceEquals(cecil, null))
                {
                    h.Patch(cecil, new HarmonyMethod(typeof(DllLookup).GetMethod("CecilPre", Any)));
                    on++;
                }
                Mode = on + "/2 lookups indexed";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>One line for the log and the timeline.</summary>
        public static string Summary()
        {
            // Not "lock": on net46 it compiles to a Monitor.Enter overload the game's runtime does not have.
            Monitor.Enter(s_lock);
            try
            {
                return "dll lookups=" + s_calls + " found=" + s_found + " notFound=" + (s_calls - s_found) + " distinctNotFound=" + s_missed.Count
                    + " indexMs=" + s_indexMs.ToString("0", CultureInfo.InvariantCulture) + " mode=" + Mode;
            }
            finally
            {
                Monitor.Exit(s_lock);
            }
        }

        private static List<string> Candidates(AssemblyName assemblyName, string directory)
        {
            DllIndex index;
            if (!s_roots.TryGetValue(directory, out index))
            {
                Stopwatch sw = Stopwatch.StartNew();
                index = DllIndex.Build(directory);
                sw.Stop();
                s_indexMs += sw.Elapsed.TotalMilliseconds;
                s_roots[directory] = index;
                Timeline.Write("RES", "indexed " + directory + " dirs=" + index.Dirs + " dlls=" + index.Files + " ms="
                    + sw.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
            }
            s_calls++;
            return index.Find(assemblyName.Name);
        }

        private static void Missed(AssemblyName assemblyName, string directory)
        {
            string name = assemblyName.Name ?? "";
            int n;
            if (s_missed.TryGetValue(name, out n))
            {
                s_missed[name] = n + 1;
                return;
            }
            if (s_missed.Count >= MaxNamed) return;
            s_missed[name] = 1;
            Timeline.Write("RES", "not found " + name + " by=" + Asker());
        }

        /// <summary>The first few callers that are not the runtime, BepInEx or this mod.</summary>
        private static string Asker()
        {
            try
            {
                StackTrace st = new StackTrace(false);
                string by = "";
                int taken = 0;
                for (int i = 0; i < st.FrameCount && taken < 4; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (ReferenceEquals(m, null) || ReferenceEquals(m.DeclaringType, null)) continue;
                    string asm = m.DeclaringType.Assembly.GetName().Name;
                    if (asm == "mscorlib" || asm == "FastLoadPatcher" || asm == "BepInEx.Preloader" || asm == "System" || asm == "System.Core"
                        || asm.StartsWith("MonoMod", StringComparison.Ordinal)) continue;
                    by += (taken == 0 ? "" : " < ") + asm + ":" + m.DeclaringType.Name + "." + m.Name;
                    taken++;
                }
                return by.Length == 0 ? "(runtime)" : by;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static bool AsmPre(AssemblyName assemblyName, string directory, ref Assembly assembly, ref bool __result)
        {
            if (s_broken || ReferenceEquals(assemblyName, null) || directory == null) return true;
            try
            {
                Monitor.Enter(s_lock);
                try
                {
                    assembly = null;
                    __result = false;
                    List<string> paths = Candidates(assemblyName, directory);
                    if (paths != null)
                    {
                        for (int i = 0; i < paths.Count; i++)
                        {
                            try
                            {
                                assembly = Assembly.LoadFile(paths[i]);
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            s_found++;
                            __result = true;
                            return false;
                        }
                    }
                    Missed(assemblyName, directory);
                    return false;
                }
                finally
                {
                    Monitor.Exit(s_lock);
                }
            }
            catch (Exception e)
            {
                s_broken = true;
                Timeline.Write("ERR", "dll lookup switched off: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static bool CecilPre(AssemblyName assemblyName, string directory, ReaderParameters readerParameters, ref AssemblyDefinition assembly, ref bool __result)
        {
            if (s_broken || ReferenceEquals(assemblyName, null) || directory == null) return true;
            try
            {
                Monitor.Enter(s_lock);
                try
                {
                    assembly = null;
                    __result = false;
                    List<string> paths = Candidates(assemblyName, directory);
                    if (paths != null)
                    {
                        for (int i = 0; i < paths.Count; i++)
                        {
                            try
                            {
                                assembly = AssemblyDefinition.ReadAssembly(paths[i], readerParameters);
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            s_found++;
                            __result = true;
                            return false;
                        }
                    }
                    Missed(assemblyName, directory);
                    return false;
                }
                finally
                {
                    Monitor.Exit(s_lock);
                }
            }
            catch (Exception e)
            {
                s_broken = true;
                Timeline.Write("ERR", "dll lookup switched off: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }
    }
}
