using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace FastLoad.Early
{
    /// <summary>
    /// Probe only: times Harmony's "look through every loaded assembly" helpers, which plugins call while they load.
    /// A call that holds the main thread for SlowMs or more gets a timeline line. Changes nothing.
    /// </summary>
    public static class ScanTimers
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private const double SlowMs = 100.0;

        public static string Mode = "off";

        public static void Install()
        {
            try
            {
                Harmony h = new Harmony("ken.h3vr.fastload.scantimers");
                Type at = typeof(AccessTools);
                int on = 0;
                on += Patch(h, at.GetMethod("TypeByName", Any, null, new Type[] { typeof(string) }, null), "NamePost");
                on += Patch(h, at.GetMethod("GetTypesFromAssembly", Any, null, new Type[] { typeof(Assembly) }, null), "AsmPost");
                Mode = on + "/2 timers";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static int Patch(Harmony h, MethodInfo target, string post)
        {
            if (ReferenceEquals(target, null)) return 0;
            h.Patch(target, new HarmonyMethod(typeof(ScanTimers).GetMethod("Pre", Any)), new HarmonyMethod(typeof(ScanTimers).GetMethod(post, Any)));
            return 1;
        }

        private static void Pre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static double Ms(long t0)
        {
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        private static void NamePost(long __state, string name)
        {
            try
            {
                double ms = Ms(__state);
                if (ms >= SlowMs) Timeline.Write("SLO", "TypeByName " + name + " ms=" + ms.ToString("0", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
            }
        }

        private static void AsmPost(long __state, Assembly assembly)
        {
            try
            {
                double ms = Ms(__state);
                if (ms >= SlowMs) Timeline.Write("SLO", "GetTypes " + (ReferenceEquals(assembly, null) ? "?" : assembly.GetName().Name) + " ms=" + ms.ToString("0", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
            }
        }
    }
}
