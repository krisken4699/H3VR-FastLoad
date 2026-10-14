using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FastLoad.Early;
using FistVR;
using HarmonyLib;
using ModularWorkshop;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// The one gate every modular part goes through before it is put on a gun: the four configure methods of
    /// ModularWorkshop (general points, barrel, handguard, stock). Whoever calls them (a workshop panel, a vault file,
    /// Take and Hold's random parts, H3MP, other mods) gets a real part:
    ///   - a part that is only a stand-in, or not loaded at all, has its bundle loaded right there, synchronously
    ///     (the bundle that holds it when that is known, otherwise every bundle of its group, as without FastLoad);
    ///   - if that bundle is already on its way in the background (cannot be finished from here), the gun keeps the
    ///     part it has and the call is made again the moment the bundle is in. A stand-in is never put on a gun.
    /// Found 2026-10-15 by reading ModularWorkshop: barrel, handguard and stock are put on by their own methods,
    /// which neither the optimizer nor FastLoad were watching (vault guns got the wrong barrel since part-level
    /// loading; with stand-ins they could get an empty stand-in).
    /// </summary>
    public static class MwoEnsure
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static MethodInfo s_syncOne;
        private static HashSet<string> s_started;
        private static IDictionary s_groupToBundles;

        private sealed class Again
        {
            public object Target;
            public MethodInfo Method;
            public object[] Args;
            public List<string> Paths;
            public string What;
        }

        private static readonly List<Again> s_again = new List<Again>();
        public static int Loaded;
        public static int Deferred;

        public static void Install(Harmony h, MethodInfo syncOne, FieldInfo started, FieldInfo groupToBundles)
        {
            // Test-only: a file named nogate.txt next to the run logs leaves the gate out, to see the game without it.
            if (System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FastLoad"), "nogate.txt")))
            {
                Timeline.Write("DEF", "modular parts: part gate left out (nogate.txt)");
                return;
            }
            s_syncOne = syncOne;
            s_started = ReferenceEquals(started, null) ? null : started.GetValue(null) as HashSet<string>;
            s_groupToBundles = ReferenceEquals(groupToBundles, null) ? null : groupToBundles.GetValue(null) as IDictionary;
            HarmonyMethod general = new HarmonyMethod(typeof(MwoEnsure).GetMethod("GeneralPre", Any));
            general.priority = Priority.First;
            h.Patch(typeof(ModularFVRFireArm).GetMethod("ConfigureModularWeaponPart", Any), general);
            h.Patch(typeof(ModularFVRPhysicalObject).GetMethod("ConfigureModularWeaponPart", Any), general);
            string[] names = { "ConfigureModularBarrel", "ConfigureModularHandguard", "ConfigureModularStock" };
            string[] pre = { "BarrelPre", "HandguardPre", "StockPre" };
            for (int i = 0; i < names.Length; i++)
            {
                HarmonyMethod m = new HarmonyMethod(typeof(MwoEnsure).GetMethod(pre[i], Any));
                m.priority = Priority.First;
                h.Patch(typeof(ModularFVRFireArm).GetMethod(names[i], Any), m);
            }
        }

        // ---- the four gates

        // Arguments for calling again later are built here: the original's own optional arguments (old sub-parts and
        // skins) are left at their defaults, as every caller outside the workshop's own recursion passes them.

        private static bool GeneralPre(object __instance, MethodBase __originalMethod, ModularWeaponPartsAttachmentPoint modularWeaponPartsAttachmentPoint, string selectedPart, bool isRandomized)
        {
            if (modularWeaponPartsAttachmentPoint == null) return true;
            return Gate(__instance, __originalMethod, Args(__originalMethod, modularWeaponPartsAttachmentPoint, selectedPart, isRandomized), modularWeaponPartsAttachmentPoint.ModularPartsGroupID, selectedPart);
        }

        private static bool BarrelPre(ModularFVRFireArm __instance, MethodBase __originalMethod, string selectedPart, bool isRandomized)
        {
            return __instance == null || __instance.ModularBarrelAttachmentPoint == null
                || Gate(__instance, __originalMethod, Args(__originalMethod, selectedPart, isRandomized), __instance.ModularBarrelAttachmentPoint.ModularPartsGroupID, selectedPart);
        }

        private static bool HandguardPre(ModularFVRFireArm __instance, MethodBase __originalMethod, string selectedPart, bool isRandomized)
        {
            return __instance == null || __instance.ModularHandguardAttachmentPoint == null
                || Gate(__instance, __originalMethod, Args(__originalMethod, selectedPart, isRandomized), __instance.ModularHandguardAttachmentPoint.ModularPartsGroupID, selectedPart);
        }

        private static bool StockPre(ModularFVRFireArm __instance, MethodBase __originalMethod, string selectedPart, bool isRandomized)
        {
            return __instance == null || __instance.ModularStockAttachmentPoint == null
                || Gate(__instance, __originalMethod, Args(__originalMethod, selectedPart, isRandomized), __instance.ModularStockAttachmentPoint.ModularPartsGroupID, selectedPart);
        }

        /// <summary>The given leading arguments, then each remaining parameter's default value.</summary>
        private static object[] Args(MethodBase method, params object[] first)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = i < first.Length ? first[i] : ps[i].IsOptional ? ps[i].DefaultValue : null;
            return args;
        }

        /// <summary>True: go on (the part is real now, or it is not ours to judge). False: the original is skipped and called again later.</summary>
        private static bool Gate(object target, MethodBase method, object[] args, string group, string part)
        {
            try
            {
                if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(part) || IsReal(group, part)) return true;
                List<string> paths = BundlesFor(group, part);
                List<string> waiting = new List<string>();
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    if (MwoParts.IsRegistered(path)) continue;
                    if (s_started != null && s_started.Contains(path))
                    {
                        waiting.Add(path);
                        continue;
                    }
                    s_syncOne.Invoke(null, new object[] { path });
                    Loaded++;
                }
                if (IsReal(group, part)) return true;
                if (waiting.Count == 0 && !MwoStubs.IsStub(group, part))
                {
                    // Not in any bundle at all: the workshop says so itself ("does not contain part"), as it would without FastLoad.
                    return true;
                }
                Again again = new Again();
                again.Target = target;
                again.Method = method as MethodInfo;
                again.Args = args;
                again.Paths = waiting.Count > 0 ? waiting : paths;
                again.What = group + " / " + part;
                s_again.Add(again);
                Deferred++;
                Timeline.Write("DEF", "modular parts: '" + part + "' (" + group + ") waits for " + again.Paths.Count + " bundle(s) already loading; the gun keeps its part until then");
                return false;
            }
            catch (Exception e)
            {
                Timeline.Write("DEF", "modular parts: gate failed for '" + part + "' (" + group + "): " + e.GetType().Name + ": " + e.Message + "; left to the workshop");
                return !MwoStubs.IsStub(group, part);
            }
        }

        /// <summary>A loaded, real part of that name is in the group's list.</summary>
        private static bool IsReal(string group, string part)
        {
            if (MwoStubs.IsStub(group, part)) return false;
            ModularWorkshopPartsDefinition def;
            if (!ModularWorkshopManager.ModularWorkshopPartsGroupsDictionary.TryGetValue(group, out def) || def == null || def.ModularPrefabs == null) return false;
            for (int i = 0; i < def.ModularPrefabs.Count; i++)
            {
                GameObject prefab = def.ModularPrefabs[i];
                ModularWeaponPart p = prefab != null ? prefab.GetComponent<ModularWeaponPart>() : null;
                if (p != null && p.Name == part) return true;
            }
            return false;
        }

        /// <summary>The bundle a stand-in stands for; else the bundles known to hold that part; else every bundle of the group.</summary>
        private static List<string> BundlesFor(string group, string part)
        {
            List<string> paths = new List<string>();
            string stub = MwoStubs.PathOf(group, part);
            if (stub != null)
            {
                paths.Add(stub);
                return paths;
            }
            List<string> all = s_groupToBundles != null && s_groupToBundles.Contains(group) ? s_groupToBundles[group] as List<string> : null;
            if (all == null) return paths;
            string key = PartIndex.PartKey(group, part);
            for (int i = 0; i < all.Count; i++)
            {
                PartIndex.Entry e = MwoParts.Look(all[i]);
                if (e != null && e.Parts.ContainsKey(key)) paths.Add(all[i]);
            }
            if (paths.Count > 0) return paths;
            // Never seen: any bundle of the group may hold it, as the optimizer would load them.
            for (int i = 0; i < all.Count; i++) if (MwoParts.Look(all[i]) == null) paths.Add(all[i]);
            return paths;
        }

        /// <summary>Called every frame: calls made to wait are made again once their bundles are in.</summary>
        public static void Tick()
        {
            for (int i = s_again.Count - 1; i >= 0; i--)
            {
                Again a = s_again[i];
                bool ready = true;
                for (int k = 0; k < a.Paths.Count && ready; k++) ready = MwoParts.IsRegistered(a.Paths[k]);
                if (!ready) continue;
                s_again.RemoveAt(i);
                if (!Alive(a.Target) || ReferenceEquals(a.Method, null)) continue;
                try
                {
                    a.Method.Invoke(a.Target, a.Args);
                    Timeline.Write("DEF", "modular parts: '" + a.What + "' put on now that its bundle is in");
                }
                catch (Exception e)
                {
                    Timeline.Write("DEF", "modular parts: '" + a.What + "' could not be put on after loading: " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message);
                }
            }
        }

        private static bool Alive(object target)
        {
            ModularFVRFireArm arm = target as ModularFVRFireArm;
            if (arm != null) return arm.FireArm != null;
            UnityEngine.Object o = target as UnityEngine.Object;
            return o != null;
        }
    }
}
