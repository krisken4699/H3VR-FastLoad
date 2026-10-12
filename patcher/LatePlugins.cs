using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace FastLoad.Early
{
    /// <summary>
    /// "[Later] Plugins": BepInEx starts every plugin before the first frame, one after another. The plugins named in
    /// the config are held back here (BepInEx's "start this plugin" call returns nothing for them) and started by
    /// LoadNext once the game is on screen. A plugin that needs a held one is held too, so the order between them
    /// stays what BepInEx worked out.
    ///
    /// Has Unity types in it: only reached through LateGate, at "Chainloader started".
    /// </summary>
    public static class LatePlugins
    {
        public sealed class Held
        {
            public Type Type;
            public string Guid;
            public string Name;
            /// <summary>Why it is held: "listed", or "needs &lt;guid&gt;".</summary>
            public string Why;
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static readonly List<Held> s_held = new List<Held>();
        private static int s_next;
        private static bool s_holding;

        public static string Mode = "off";

        public static int HeldCount { get { return s_held.Count; } }
        public static int Remaining { get { return s_held.Count - s_next; } }

        public static void Install()
        {
            if (LaterList.Wanted.Count == 0) return;
            try
            {
                Harmony h = new Harmony("invent60.h3vr.fastload.later");
                MethodInfo add = typeof(GameObject).GetMethod("AddComponent", Any, null, new Type[] { typeof(Type) }, null);
                if (ReferenceEquals(add, null))
                {
                    Mode = "failed: GameObject.AddComponent(Type) not found";
                    return;
                }
                h.Patch(add, new HarmonyMethod(typeof(LatePlugins).GetMethod("AddPre", Any)));
                s_holding = true;
                Mode = "holding " + LaterList.Wanted.Count + " listed";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>BepInEx is done with its own round: from here on every AddComponent passes untouched.</summary>
        public static void Release()
        {
            s_holding = false;
        }

        private static bool AddPre(GameObject __instance, Type componentType, ref Component __result)
        {
            if (!s_holding) return true;
            try
            {
                if (ReferenceEquals(componentType, null) || !ReferenceEquals(__instance, Chainloader.ManagerObject)) return true;
                if (!typeof(BaseUnityPlugin).IsAssignableFrom(componentType)) return true;

                object[] meta = componentType.GetCustomAttributes(typeof(BepInPlugin), false);
                if (meta.Length == 0) return true;
                BepInPlugin plugin = (BepInPlugin)meta[0];

                string why = null;
                if (LaterList.Has(LaterList.Wanted, plugin.GUID, plugin.Name)) why = "listed";
                else
                {
                    object[] deps = componentType.GetCustomAttributes(typeof(BepInDependency), true);
                    for (int i = 0; i < deps.Length && why == null; i++)
                    {
                        string dep = ((BepInDependency)deps[i]).DependencyGUID;
                        for (int k = 0; k < s_held.Count; k++)
                        {
                            if (string.Equals(s_held[k].Guid, dep, StringComparison.OrdinalIgnoreCase)) why = "needs " + dep;
                        }
                    }
                }
                if (why == null) return true;

                // Loaders that take part in the staged start-up must be there when the stages run.
                for (Type t = componentType.BaseType; !ReferenceEquals(t, null); t = t.BaseType)
                {
                    if (t.Name == "StratumPlugin" || t.Name == "DeliBehaviour")
                    {
                        Timeline.Write("DEF", "not held " + plugin.Name + " (" + plugin.GUID + "): it is a " + t.Name + " and must start with the others");
                        return true;
                    }
                }

                Held held = new Held();
                held.Type = componentType;
                held.Guid = plugin.GUID;
                held.Name = plugin.Name;
                held.Why = why;
                s_held.Add(held);
                Timeline.Write("DEF", "held " + plugin.Name + " (" + plugin.GUID + ") " + why);
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "later plugins: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        /// <summary>Name of the plugin LoadNext would start now, or "" when none is left.</summary>
        public static string NextName()
        {
            return s_next < s_held.Count ? s_held[s_next].Name : "";
        }

        /// <summary>Starts the next held plugin, as BepInEx would have. False when none is left.</summary>
        public static bool LoadNext(out string report)
        {
            report = "";
            if (s_next >= s_held.Count) return false;
            Held held = s_held[s_next++];
            s_holding = false;
            long commitBefore = Memory.Commit();
            Stopwatch sw = Stopwatch.StartNew();
            string error = null;
            try
            {
                Timeline.Write("DEF", "starting " + held.Name + " (" + held.Guid + ")");
                Component made = Chainloader.ManagerObject.AddComponent(held.Type);
                BaseUnityPlugin instance = made as BaseUnityPlugin;
                PluginInfo info;
                if (Chainloader.PluginInfos.TryGetValue(held.Guid, out info) && !ReferenceEquals(info, null))
                {
                    PropertyInfo prop = typeof(PluginInfo).GetProperty("Instance", Any);
                    MethodInfo set = ReferenceEquals(prop, null) ? null : prop.GetSetMethod(true);
                    if (!ReferenceEquals(set, null)) set.Invoke(info, new object[] { instance });
                }
                FieldInfo list = typeof(Chainloader).GetField("_plugins", Any);
                List<BaseUnityPlugin> plugins = ReferenceEquals(list, null) ? null : list.GetValue(null) as List<BaseUnityPlugin>;
                if (plugins != null && !ReferenceEquals(instance, null)) plugins.Add(instance);
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                error = inner.GetType().Name + ": " + inner.Message;
            }
            sw.Stop();
            long grew = Memory.Commit() - commitBefore;
            report = held.Name + " " + (error == null ? "started" : "FAILED (" + error + ")") + " in "
                + (sw.Elapsed.TotalMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s, memory "
                + (grew >= 0 ? "+" : "") + (grew / 1048576).ToString(CultureInfo.InvariantCulture) + " MB";
            Timeline.Write("DEF", report);
            return true;
        }

        /// <summary>"Sosig Hat Realism (listed), X (needs y)".</summary>
        public static string HeldText()
        {
            string s = "";
            for (int i = 0; i < s_held.Count; i++) s += (i == 0 ? "" : ", ") + s_held[i].Name + " (" + s_held[i].Why + ")";
            return s;
        }
    }
}
