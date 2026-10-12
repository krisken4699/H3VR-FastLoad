using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using HarmonyLib;
using ModularWorkshop;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// "[OnDemand] ModularParts": ModulWorkshop_Optimizer loads, for a modular gun, every bundle that holds any part
    /// for any of the gun's part points (48 bundles for one AR on the test profile, about a minute). A gun only
    /// shows one part a point. Here the optimizer's own bundle lists are trimmed to:
    ///   - bundles holding a part that is on the gun (or is being put on it right now), or skins for such a part;
    ///   - the other bundles from the same mod folders as those (a mod split over several files may share
    ///     materials between them);
    ///   - bundles never seen before (loading them is how their contents become known);
    ///   - everything for a part group, when a part that is wanted is not there otherwise.
    /// The rest of a part group is loaded when a workshop panel for that group appears, and the panel is refreshed
    /// when it is in. What bundle holds what is learned as bundles load and kept in BepInEx/cache between starts,
    /// so the first modular gun after installing loads everything once.
    ///
    /// The optimizer does all the loading, as before; only its lists are shorter.
    /// </summary>
    public static class MwoParts
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static ManualLogSource s_log;
        private static PartIndex s_index = new PartIndex();
        private static string s_indexPath = "";
        private static bool s_indexDirty;
        private static readonly System.Diagnostics.Stopwatch s_sinceDirty = new System.Diagnostics.Stopwatch();

        private static MethodInfo s_isRegistered;
        private static MethodInfo s_ensureAsync;
        private static MethodInfo s_runCoroutine;
        private static FieldInfo s_groupToBundles;

        /// <summary>Part being put on a point right now, as "group|part".</summary>
        private static string s_configuring;
        /// <summary>Groups whose every bundle is wanted (a workshop panel for them is open).</summary>
        private static readonly Dictionary<string, bool> s_wholeGroups = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, KeyValuePair<int, Dictionary<string, bool>>> s_registeredNames = new Dictionary<string, KeyValuePair<int, Dictionary<string, bool>>>();

        public static string Mode = "off";
        public static int Trimmed;
        public static int Kept;

        public static void Install(ManualLogSource log, Harmony h, Type loader)
        {
            s_log = log;
            try
            {
                Assembly a = loader.Assembly;
                Type repo = null, registerer = null, runner = null;
                foreach (Type t in a.GetTypes())
                {
                    if (t.Name == "ModularBundlesMetadataRepository") repo = t;
                    else if (t.Name == "ModularWorkshopRegisterer") registerer = t;
                    else if (t.Name == "CoroutineRunner") runner = t;
                }
                s_isRegistered = loader.GetMethod("IsBundleRegistered", Any);
                s_ensureAsync = loader.GetMethod("EnsureBundlesAreLoadedAndRegisteredAsync", Any);
                MethodInfo syncOne = loader.GetMethod("LoadAndRegisterBundle", Any);
                MethodInfo register = ReferenceEquals(registerer, null) ? null : registerer.GetMethod("RegisterDefinitionsFromBundle", Any);
                s_runCoroutine = ReferenceEquals(runner, null) ? null : runner.GetMethod("Run", Any);
                s_groupToBundles = ReferenceEquals(repo, null) ? null : repo.GetField("GroupIDToBundlesDic", Any);
                MethodInfo byArm = ReferenceEquals(repo, null) ? null : repo.GetMethod("GetBundlesByFireArm", Any);
                MethodInfo byObj = ReferenceEquals(repo, null) ? null : repo.GetMethod("GetBundlesByObject", Any);
                MethodInfo byPart = ReferenceEquals(repo, null) ? null : repo.GetMethod("GetBundlesByModularPart", Any);
                if (ReferenceEquals(s_isRegistered, null) || ReferenceEquals(s_ensureAsync, null) || ReferenceEquals(syncOne, null) || ReferenceEquals(register, null)
                    || ReferenceEquals(s_runCoroutine, null) || ReferenceEquals(s_groupToBundles, null) || ReferenceEquals(byArm, null) || ReferenceEquals(byObj, null)
                    || ReferenceEquals(byPart, null))
                {
                    Mode = "ModulWorkshop_Optimizer has other names than expected (another version?): every part bundle is loaded, as without this";
                    return;
                }

                s_indexPath = Path.Combine(Paths.CachePath, "FastLoad.modularparts.txt");
                try
                {
                    if (File.Exists(s_indexPath)) s_index = PartIndex.Parse(File.ReadAllText(s_indexPath));
                }
                catch (Exception)
                {
                    s_index = new PartIndex();
                }

                Type me = typeof(MwoParts);
                h.Patch(register, new HarmonyMethod(me.GetMethod("RegisterPre", Any)));
                h.Patch(syncOne, new HarmonyMethod(me.GetMethod("SyncPre", Any)), new HarmonyMethod(me.GetMethod("SyncPost", Any)));
                h.Patch(byArm, null, new HarmonyMethod(me.GetMethod("ByArmPost", Any)));
                h.Patch(byObj, null, new HarmonyMethod(me.GetMethod("ByObjPost", Any)));
                h.Patch(byPart, null, new HarmonyMethod(me.GetMethod("ByPartPost", Any)));

                HarmonyMethod first = new HarmonyMethod(me.GetMethod("ConfigurePre", Any));
                first.priority = Priority.First;
                HarmonyMethod last = new HarmonyMethod(me.GetMethod("ConfigurePost", Any));
                h.Patch(typeof(ModularFVRFireArm).GetMethod("ConfigureModularWeaponPart", Any), prefix: first, finalizer: last);
                h.Patch(typeof(ModularFVRPhysicalObject).GetMethod("ConfigureModularWeaponPart", Any), prefix: first, finalizer: last);
                h.Patch(typeof(ModularWorkshopUI).GetMethod("InitializeArrays", Any), null, new HarmonyMethod(me.GetMethod("PanelPost", Any)));
                h.Patch(typeof(ModularWorkshopUI).GetMethod("PButton_ShowUI", Any), null, new HarmonyMethod(me.GetMethod("PanelPost", Any)));
                Mode = "on, " + s_index.Count + " bundles known from earlier runs";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message + " (every part bundle is loaded, as without this)";
            }
        }

        // ---- learning what a bundle holds

        private static void SyncPre(string bundlePath)
        {
            s_syncPath = bundlePath;
        }

        private static void SyncPost()
        {
            s_syncPath = null;
        }

        /// <summary>Bundle file a synchronous load of the optimizer is on.</summary>
        private static string s_syncPath;

        private static void RegisterPre(UnityEngine.Object[] assets)
        {
            try
            {
                string path = s_syncPath ?? MwoBridge.CurrentPath;
                if (path == null || assets == null) return;
                FileInfo file = new FileInfo(path);
                if (!file.Exists) return;
                PartIndex.Entry e = s_index.Learn(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks);
                for (int i = 0; i < assets.Length; i++)
                {
                    ModularWorkshopPartsDefinition parts = assets[i] as ModularWorkshopPartsDefinition;
                    if (parts != null)
                    {
                        PartIndex.AddPart(e, parts.ModularPartsGroupID, null);
                        for (int k = 0; parts.ModularPrefabs != null && k < parts.ModularPrefabs.Count; k++)
                        {
                            GameObject prefab = parts.ModularPrefabs[k];
                            ModularWeaponPart part = prefab != null ? prefab.GetComponent<ModularWeaponPart>() : null;
                            if (part != null) PartIndex.AddPart(e, parts.ModularPartsGroupID, part.Name);
                        }
                        continue;
                    }
                    ModularWorkshopSkinsDefinition skins = assets[i] as ModularWorkshopSkinsDefinition;
                    if (skins != null) PartIndex.AddSkin(e, skins.ModularPartsGroupID + "/" + skins.PartName);
                }
                s_indexDirty = true;
                s_sinceDirty.Reset();
                s_sinceDirty.Start();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Called every frame by the plugin: writes the index a few seconds after the last thing was learned.</summary>
        public static void Tick()
        {
            if (!s_indexDirty || s_sinceDirty.Elapsed.TotalSeconds < 5.0) return;
            s_indexDirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_indexPath));
                File.WriteAllText(s_indexPath, s_index.Serialize());
                Timeline.Write("DEF", "modular parts: index written, " + s_index.Count + " bundles known");
            }
            catch (Exception)
            {
            }
        }

        // ---- trimming the optimizer's lists

        private static void ConfigurePre(ModularWeaponPartsAttachmentPoint modularWeaponPartsAttachmentPoint, string selectedPart)
        {
            s_configuring = modularWeaponPartsAttachmentPoint != null ? PartIndex.PartKey(modularWeaponPartsAttachmentPoint.ModularPartsGroupID, selectedPart) : null;
        }

        private static void ConfigurePost()
        {
            s_configuring = null;
        }

        private static void ByArmPost(ModularFVRFireArm modularFireArm, FVRFireArm fireArm, ref List<string> __result)
        {
            try
            {
                if (modularFireArm == null) return;
                string skin = modularFireArm.SkinPath;
                if (string.IsNullOrEmpty(skin) && fireArm != null && fireArm.ObjectWrapper != null) skin = fireArm.ObjectWrapper.ItemID + "/Receiver";
                __result = Trim(__result, modularFireArm.AllAttachmentPoints, null, skin);
            }
            catch (Exception)
            {
            }
        }

        private static void ByObjPost(ModularFVRPhysicalObject modularObject, ref List<string> __result)
        {
            try
            {
                if (modularObject == null) return;
                string skin = modularObject.SkinPath;
                if (string.IsNullOrEmpty(skin) && modularObject.MainObject != null && modularObject.MainObject.ObjectWrapper != null)
                    skin = modularObject.MainObject.ObjectWrapper.ItemID + "/Receiver";
                __result = Trim(__result, modularObject.AllAttachmentPoints, null, skin);
            }
            catch (Exception)
            {
            }
        }

        private static void ByPartPost(ModularWeaponPartsAttachmentPoint modularPart, ref List<string> __result)
        {
            try
            {
                if (modularPart != null) __result = Trim(__result, null, modularPart, null);
            }
            catch (Exception)
            {
            }
        }

        private static List<string> Trim(List<string> all, Dictionary<string, ModularWeaponPartsAttachmentPoint> points, ModularWeaponPartsAttachmentPoint one, string receiverSkin)
        {
            if (all == null || all.Count == 0) return all;
            Dictionary<string, bool> parts = new Dictionary<string, bool>(StringComparer.Ordinal);
            Dictionary<string, bool> skins = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> wanted = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(receiverSkin)) skins[receiverSkin] = true;
            if (points != null) foreach (KeyValuePair<string, ModularWeaponPartsAttachmentPoint> pair in points) Want(pair.Value, parts, skins, wanted);
            Want(one, parts, skins, wanted);
            if (s_configuring != null)
            {
                parts[s_configuring] = true;
                int bar = s_configuring.IndexOf('|');
                skins[s_configuring.Substring(0, bar) + "/" + s_configuring.Substring(bar + 1)] = true;
                wanted.Add(new KeyValuePair<string, string>(s_configuring.Substring(0, bar), s_configuring.Substring(bar + 1)));
            }

            List<string> keep = new List<string>();
            List<string> left = new List<string>();
            Dictionary<string, PartIndex.Entry> known = new Dictionary<string, PartIndex.Entry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                string path = all[i];
                if (path == null || known.ContainsKey(path)) continue;
                PartIndex.Entry e = Registered(path) ? null : Look(path);
                known[path] = e;
                if (Registered(path) || PartIndex.Needed(e, parts, skins, s_wholeGroups))
                {
                    keep.Add(path);
                    folders[BundleLedger.ModOf(path)] = true;
                }
                else left.Add(path);
            }

            // A part that is wanted, is not there yet, and is in none of the bundles kept: take everything for its group.
            for (int w = 0; w < wanted.Count; w++)
            {
                string group = wanted[w].Key;
                string key = PartIndex.PartKey(group, wanted[w].Value);
                if (wanted[w].Value.Length == 0 || IsThere(group, wanted[w].Value)) continue;
                bool provided = false;
                for (int i = 0; i < keep.Count && !provided; i++)
                {
                    PartIndex.Entry e = known[keep[i]];
                    provided = e != null && e.Parts.ContainsKey(key);
                }
                if (provided) continue;
                for (int i = left.Count - 1; i >= 0; i--)
                {
                    PartIndex.Entry e = known[left[i]];
                    if (e == null || !e.Groups.ContainsKey(group)) continue;
                    keep.Add(left[i]);
                    folders[BundleLedger.ModOf(left[i])] = true;
                    left.RemoveAt(i);
                }
            }

            // A mod split over several files may share materials between them.
            for (int i = left.Count - 1; i >= 0; i--)
            {
                if (!folders.ContainsKey(BundleLedger.ModOf(left[i]))) continue;
                keep.Add(left[i]);
                left.RemoveAt(i);
            }

            if (left.Count > 0)
            {
                Trimmed += left.Count;
                Kept += keep.Count;
                Timeline.Write("DEF", "modular parts: " + keep.Count + " of " + (keep.Count + left.Count) + " part bundles needed now; "
                    + left.Count + " left for when a workshop panel asks");
            }
            return keep;
        }

        private static void Want(ModularWeaponPartsAttachmentPoint point, Dictionary<string, bool> parts, Dictionary<string, bool> skins, List<KeyValuePair<string, string>> wanted)
        {
            if (point == null || string.IsNullOrEmpty(point.ModularPartsGroupID)) return;
            string part = point.SelectedModularWeaponPart ?? "";
            parts[PartIndex.PartKey(point.ModularPartsGroupID, part)] = true;
            skins[point.ModularPartsGroupID + "/" + part] = true;
            wanted.Add(new KeyValuePair<string, string>(point.ModularPartsGroupID, part));
        }

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

        private static PartIndex.Entry Look(string path)
        {
            try
            {
                FileInfo file = new FileInfo(path);
                return file.Exists ? s_index.Find(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether the workshop knows a part of that name in that group already.</summary>
        private static bool IsThere(string group, string part)
        {
            ModularWorkshopPartsDefinition def;
            if (!ModularWorkshopManager.ModularWorkshopPartsGroupsDictionary.TryGetValue(group, out def) || def == null || def.ModularPrefabs == null) return false;
            KeyValuePair<int, Dictionary<string, bool>> cached;
            if (!s_registeredNames.TryGetValue(group, out cached) || cached.Key != def.ModularPrefabs.Count)
            {
                Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.Ordinal);
                for (int i = 0; i < def.ModularPrefabs.Count; i++)
                {
                    GameObject prefab = def.ModularPrefabs[i];
                    ModularWeaponPart p = prefab != null ? prefab.GetComponent<ModularWeaponPart>() : null;
                    if (p != null && p.Name != null) names[p.Name] = true;
                }
                cached = new KeyValuePair<int, Dictionary<string, bool>>(def.ModularPrefabs.Count, names);
                s_registeredNames[group] = cached;
            }
            return cached.Value.ContainsKey(part);
        }

        // ---- the rest of a group, when a workshop panel for it appears

        private static void PanelPost(ModularWorkshopUI __instance)
        {
            try
            {
                string group = __instance != null ? __instance.ModularPartsGroupID : null;
                if (string.IsNullOrEmpty(group) || s_wholeGroups.ContainsKey(group)) return;
                s_wholeGroups[group] = true;
                IDictionary dic = s_groupToBundles.GetValue(null) as IDictionary;
                List<string> all = dic != null && dic.Contains(group) ? dic[group] as List<string> : null;
                if (all == null) return;
                List<string> todo = new List<string>();
                for (int i = 0; i < all.Count; i++) if (!Registered(all[i])) todo.Add(all[i]);
                if (todo.Count == 0) return;
                Timeline.Write("DEF", "modular parts: workshop panel for '" + group + "' opened; loading its other " + todo.Count + " bundles");
                IEnumerator load = s_ensureAsync.Invoke(null, new object[] { todo }) as IEnumerator;
                if (load == null) return;
                s_runCoroutine.Invoke(null, new object[] { new Then(load, __instance) });
            }
            catch (Exception e)
            {
                if (s_log != null) s_log.LogWarning("Modular parts: could not load the rest of a part group for the workshop panel: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Runs the optimizer's load routine, then shows the panel what is there now.</summary>
        private sealed class Then : IEnumerator
        {
            private readonly IEnumerator m_inner;
            private readonly ModularWorkshopUI m_panel;
            private bool m_done;

            public Then(IEnumerator inner, ModularWorkshopUI panel)
            {
                m_inner = inner;
                m_panel = panel;
            }

            public object Current
            {
                get { return m_inner.Current; }
            }

            public bool MoveNext()
            {
                if (m_done) return false;
                if (m_inner.MoveNext()) return true;
                m_done = true;
                try
                {
                    if (m_panel != null)
                    {
                        m_panel.InitializeArrays();
                        m_panel.UpdateDisplay();
                    }
                }
                catch (Exception)
                {
                }
                return false;
            }

            public void Reset()
            {
            }
        }
    }
}
