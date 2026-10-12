using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using FastLoad.Early;
using HarmonyLib;

namespace FastLoad
{
    /// <summary>
    /// Works with ModulWorkshop_Optimizer (Sirdoggy), which FastLoad depends on: that mod is what loads modular
    /// part bundles when a modular gun or part is spawned, instead of at game start. Nothing of it is replaced.
    /// Its on-demand bundle loads are followed here so that the loading window can name and count them, and so
    /// that background loading is sped up while a gun is waiting for them (LoadingHud).
    ///
    /// Everything is found by name at run time; a version with other names is left untouched.
    /// </summary>
    public static class MwoBridge
    {
        public const string Guid = "00.Sirdoggy.ModulWorkshopOptimizer";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static MethodInfo s_isRegistered;
        /// <summary>Bundles counted for the window already. The optimizer asks for the same bundle from several lists.</summary>
        private static readonly Dictionary<string, bool> s_announced = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> s_begun = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static string Mode = "off";
        /// <summary>"[OnDemand] ModularParts".</summary>
        public static bool PartLevel = true;

        public static void Install(ManualLogSource log)
        {
            try
            {
                Type loader = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        // Its namespace has moved between versions: found by class name inside its own assembly.
                        if (ReferenceEquals(a.GetType("Sirdoggy.ModulWorkshopOptimizer.Plugin"), null)) continue;
                        Type[] types = a.GetTypes();
                        for (int i = 0; i < types.Length; i++) if (types[i].Name == "ModularBundlesLoader") loader = types[i];
                    }
                    catch (Exception)
                    {
                    }
                    if (!ReferenceEquals(loader, null)) break;
                }
                if (ReferenceEquals(loader, null))
                {
                    Mode = "ModulWorkshop_Optimizer's loader class not found";
                    return;
                }
                MethodInfo one = loader.GetMethod("LoadAndRegisterBundleAsync", Any);
                MethodInfo many = loader.GetMethod("EnsureBundlesAreLoadedAndRegisteredAsync", Any);
                s_isRegistered = loader.GetMethod("IsBundleRegistered", Any);
                if (ReferenceEquals(one, null) || ReferenceEquals(many, null) || ReferenceEquals(s_isRegistered, null)
                    || !ReferenceEquals(one.ReturnType, typeof(IEnumerator)))
                {
                    Mode = "ModulWorkshop_Optimizer has other method names than expected (another version?)";
                    return;
                }
                Harmony h = new Harmony("ken.h3vr.fastload.mwo");
                h.Patch(one, null, new HarmonyMethod(typeof(MwoBridge).GetMethod("OnePost", Any)));
                h.Patch(many, new HarmonyMethod(typeof(MwoBridge).GetMethod("ManyPre", Any)));
                Mode = "on";
                if (PartLevel) PartsDoor(log, h, loader);
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            if (Mode != "on") log.LogWarning("ModulWorkshop_Optimizer bridge: " + Mode + ". Modular loads work as without it; the loading window does not count them.");
        }

        /// <summary>The only way to MwoParts, which has ModularWorkshop's types in it.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PartsDoor(ManualLogSource log, Harmony h, Type loader)
        {
            MwoParts.Install(log, h, loader);
            log.LogInfo("Modular parts on demand: " + MwoParts.Mode);
        }

        /// <summary>Bundle file the optimizer is working on in this very step (MwoParts reads it while the bundle registers).</summary>
        public static string CurrentPath;

        private static bool Registered(string path)
        {
            try
            {
                return (bool)s_isRegistered.Invoke(null, new object[] { path });
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A list of bundles is about to be loaded one after another: the window can show "1/24" from the start.</summary>
        private static void ManyPre(List<string> bundlePaths)
        {
            try
            {
                if (bundlePaths == null) return;
                int todo = 0;
                for (int i = 0; i < bundlePaths.Count; i++)
                {
                    string path = bundlePaths[i];
                    if (path == null || s_announced.ContainsKey(path) || Registered(path)) continue;
                    s_announced[path] = true;
                    todo++;
                }
                LoadFeed.Expect(todo, Clock.NowMs);
            }
            catch (Exception)
            {
            }
        }

        private static void OnePost(string bundlePath, ref IEnumerator __result)
        {
            try
            {
                // A second request for a bundle that is on its way only waits for the first: not a load of its own.
                if (__result == null || bundlePath == null || Registered(bundlePath) || s_begun.ContainsKey(bundlePath)) return;
                s_begun[bundlePath] = true;
                __result = new Followed(__result, bundlePath, s_announced.ContainsKey(bundlePath));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The optimizer's own load routine, unchanged, with a Begin before its first step and an End after its last.</summary>
        private sealed class Followed : IEnumerator
        {
            private readonly IEnumerator m_inner;
            private readonly string m_path;
            private int m_token;
            private bool m_over;

            private readonly bool m_announced;

            public Followed(IEnumerator inner, string path, bool announced)
            {
                m_inner = inner;
                m_path = path;
                m_announced = announced;
            }

            public object Current
            {
                get { return m_inner.Current; }
            }

            public bool MoveNext()
            {
                if (m_token == 0 && !m_over)
                    m_token = LoadFeed.Begin(BundleLedger.NameOf(m_path), BundleLedger.ModOf(m_path), true, m_announced, Clock.NowMs);
                bool more;
                string before = CurrentPath;
                CurrentPath = m_path;
                try
                {
                    more = m_inner.MoveNext();
                }
                catch (Exception)
                {
                    Close();
                    throw;
                }
                finally
                {
                    CurrentPath = before;
                }
                if (!more) Close();
                return more;
            }

            private void Close()
            {
                if (m_over) return;
                m_over = true;
                LoadFeed.End(m_token, Clock.NowMs);
            }

            public void Reset()
            {
                m_inner.Reset();
            }
        }
    }
}
