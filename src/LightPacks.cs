using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using FastLoad.Early;
using HarmonyLib;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// "[Light] Packs": when BepInEx/cache/FastLoad.light holds a light copy of a mod file (same contents, every texture
    /// without its biggest mip levels), the game is handed the light copy instead of the original. Experimental: the
    /// copies are written by a tool outside the game for now, and the full pictures do not come back yet.
    /// </summary>
    public static class LightPacks
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static string s_dir = "";
        private static readonly Dictionary<string, string> s_light = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string Mode = "off";
        public static int Redirected;

        public static void Install(ManualLogSource log)
        {
            try
            {
                s_dir = Path.Combine(Paths.CachePath, "FastLoad.light");
                if (Directory.Exists(s_dir))
                {
                    string[] files = Directory.GetFiles(s_dir);
                    for (int i = 0; i < files.Length; i++) s_light[Path.GetFileName(files[i])] = files[i];
                }
                if (s_light.Count == 0)
                {
                    Mode = "on, no light copies in " + s_dir;
                    return;
                }
                Harmony h = new Harmony("invent60.h3vr.fastload.light");
                Type ab = typeof(AssetBundle);
                int patched = 0;
                MethodInfo[] all = ab.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Name != "LoadFromFile" && all[i].Name != "LoadFromFileAsync") continue;
                    ParameterInfo[] ps = all[i].GetParameters();
                    if (ps.Length == 0 || !ReferenceEquals(ps[0].ParameterType, typeof(string)) || ps[0].Name != "path") continue;
                    h.Patch(all[i], new HarmonyMethod(typeof(LightPacks).GetMethod("PathPre", Any)));
                    patched++;
                }
                Mode = "on, " + s_light.Count + " light copies, " + patched + " load calls followed";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            log.LogInfo("Light packs: " + Mode);
        }

        private static void PathPre(ref string path)
        {
            try
            {
                if (path == null) return;
                string light;
                if (!s_light.TryGetValue(Path.GetFileName(path), out light)) return;
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(light), StringComparison.OrdinalIgnoreCase)) return;
                Timeline.Write("DEF", "light pack: " + Path.GetFileName(path) + " read from the light copy");
                path = light;
                Redirected++;
            }
            catch (Exception)
            {
            }
        }
    }
}
