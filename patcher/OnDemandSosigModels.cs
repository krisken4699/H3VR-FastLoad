using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace FastLoad.Early
{
    public delegate void Plain();

    /// <summary>
    /// "[OnDemand] SosigModels": CustomSosigModelFramework reads every installed model pack into memory when it
    /// starts (mesh, 12 animation clips, textures; about 9 s and 4 GB for one pack), whatever its ActiveModel setting
    /// says. With this on, a pack is read the first time it is needed: when it is the selected model and a sosig
    /// appears, or when it is picked in the wrist menu. ActiveModel = Sosig reads nothing at all.
    ///
    /// How: its pack list is trimmed to the needed packs while its Awake runs; the others get an empty entry (so the
    /// wrist menu still lists them) that is filled by the framework's own LoadModel when first asked for.
    /// Everything is found by name at run time; a framework version with other names leaves it untouched.
    /// No Unity types here: this is installed from an assembly-load event that can fire early.
    /// </summary>
    public static class OnDemandSosigModels
    {
        private const string AssemblyName = "CustomSosigModelFramework";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly string[] Filled = new string[] { "Asset", "Animations", "Materials", "EyeMaterial", "BloodVfx" };

        private static ManualLogSource s_log;
        private static Type s_plugin;
        private static Type s_model;
        private static MethodInfo s_loadModel;
        private static bool s_inAwake;
        /// <summary>Packs left out of the framework's own start, by id.</summary>
        private static readonly Dictionary<string, object> s_skipped = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Empty entries waiting to be filled, by pack id.</summary>
        private static readonly Dictionary<string, object> s_empty = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public static string Mode = "off";
        /// <summary>Called around a fill, which stops the game for as long as the pack takes to read.</summary>
        public static Plain Before;
        public static Plain After;

        /// <summary>Waits for the framework's DLL; patches it the moment it is loaded, before any of its code runs.</summary>
        public static void Watch(ManualLogSource log)
        {
            s_log = log;
            Mode = "waiting for " + AssemblyName;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                Assembly a = args.LoadedAssembly;
                if (ReferenceEquals(a, null) || a.GetName().Name != AssemblyName) return;
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                Install(a);
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            if (s_log != null && Mode.StartsWith("failed", StringComparison.Ordinal)) s_log.LogWarning("Sosig models on demand: " + Mode + ". The framework loads everything at start, as without FastLoad.");
        }

        private static void Install(Assembly a)
        {
            s_plugin = a.GetType("CustomSosigReplacer.Plugin");
            Type addon = a.GetType("CustomSosigReplacer.ModelAddon");
            s_model = ReferenceEquals(s_plugin, null) ? null : s_plugin.GetNestedType("RuntimeModel", Any);
            if (ReferenceEquals(s_plugin, null) || ReferenceEquals(addon, null) || ReferenceEquals(s_model, null))
            {
                Mode = "failed: Plugin, ModelAddon or RuntimeModel not found (another framework version?)";
                return;
            }
            MethodInfo awake = s_plugin.GetMethod("Awake", Any);
            MethodInfo discover = addon.GetMethod("Discover", Any);
            MethodInfo attach = s_plugin.GetMethod("TryAttachProxy", Any);
            MethodInfo select = s_plugin.GetMethod("SelectModel", Any);
            s_loadModel = s_plugin.GetMethod("LoadModel", Any);
            bool fields = !ReferenceEquals(s_model.GetField("Addon", Any), null);
            for (int i = 0; i < Filled.Length; i++) fields &= !ReferenceEquals(s_model.GetField(Filled[i], Any), null);
            if (ReferenceEquals(awake, null) || ReferenceEquals(discover, null) || ReferenceEquals(attach, null) || ReferenceEquals(select, null)
                || ReferenceEquals(s_loadModel, null) || !fields
                || ReferenceEquals(s_plugin.GetField("Models", Any), null) || ReferenceEquals(s_plugin.GetField("RandomPool", Any), null)
                || ReferenceEquals(s_plugin.GetField("_selectedModel", Any), null) || s_model.GetConstructors(Any).Length != 1)
            {
                Mode = "failed: a method or field this needs is not there (another framework version?)";
                return;
            }

            Type me = typeof(OnDemandSosigModels);
            Harmony h = new Harmony("invent60.h3vr.fastload.ondemand.sosigmodels");
            h.Patch(awake, new HarmonyMethod(me.GetMethod("AwakePre", Any)), new HarmonyMethod(me.GetMethod("AwakePost", Any)));
            h.Patch(discover, null, new HarmonyMethod(me.GetMethod("DiscoverPost", Any)));
            h.Patch(attach, new HarmonyMethod(me.GetMethod("AttachPre", Any)));
            h.Patch(select, new HarmonyMethod(me.GetMethod("SelectPre", Any)));
            Mode = "on";
            Timeline.Write("DEF", "sosig models on demand: patched " + AssemblyName);
        }

        private static void AwakePre()
        {
            s_inAwake = true;
        }

        /// <summary>The pack list on its way into the framework's start: only the packs the current selection shows stay.</summary>
        private static void DiscoverPost(object __result)
        {
            if (!s_inAwake) return;
            try
            {
                IDictionary found = __result as IDictionary;
                if (found == null) return;
                string selected = SelectedNow();
                if (string.Equals(selected, "Random", StringComparison.OrdinalIgnoreCase)) return;
                List<object> ids = new List<object>();
                foreach (object key in found.Keys) ids.Add(key);
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i] as string;
                    if (id == null || string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)) continue;
                    s_skipped[id] = found[ids[i]];
                    found.Remove(ids[i]);
                }
            }
            catch (Exception e)
            {
                Say("could not trim the pack list (" + e.GetType().Name + ": " + e.Message + "); everything loads at start", true);
            }
        }

        /// <summary>After the framework's start: an empty entry for every pack that was left out, so it can still be picked.</summary>
        private static void AwakePost()
        {
            s_inAwake = false;
            if (s_skipped.Count == 0) return;
            try
            {
                IDictionary models = s_plugin.GetField("Models", Any).GetValue(null) as IDictionary;
                IList pool = s_plugin.GetField("RandomPool", Any).GetValue(null) as IList;
                ConstructorInfo make = s_model.GetConstructors(Any)[0];
                string names = "";
                foreach (KeyValuePair<string, object> pair in s_skipped)
                {
                    if (models.Contains(pair.Key)) continue;
                    object[] args = new object[make.GetParameters().Length];
                    args[0] = pair.Value;
                    object empty = make.Invoke(args);
                    models.Add(pair.Key, empty);
                    pool.Add(empty);
                    s_empty[pair.Key] = empty;
                    names += (names.Length == 0 ? "" : ", ") + pair.Key;
                }
                s_skipped.Clear();
                Say("not read at start: " + names + " (selected model: " + SelectedNow() + "). Each is read when first needed.", false);
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Say("could not list the packs left out (" + inner.GetType().Name + ": " + inner.Message + "); they are missing from the wrist menu until the next start with [OnDemand] SosigModels = false", true);
            }
        }

        /// <summary>A sosig is about to get its custom look.</summary>
        private static void AttachPre()
        {
            if (s_empty.Count == 0) return;
            Need(SelectedNow());
        }

        /// <summary>A model was picked in the wrist menu.</summary>
        private static void SelectPre(string id)
        {
            if (s_empty.Count == 0) return;
            Need(id);
        }

        private static void Need(string selected)
        {
            if (string.IsNullOrEmpty(selected) || string.Equals(selected, "Sosig", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(selected, "Random", StringComparison.OrdinalIgnoreCase))
            {
                List<string> all = new List<string>(s_empty.Keys);
                for (int i = 0; i < all.Count; i++) Fill(all[i]);
                return;
            }
            if (s_empty.ContainsKey(selected)) Fill(selected);
        }

        private static void Fill(string id)
        {
            object empty = s_empty[id];
            // Out first: a pack that fails is not tried again for every sosig.
            s_empty.Remove(id);
            long before = Memory.Commit();
            Stopwatch sw = Stopwatch.StartNew();
            Call(Before);
            try
            {
                object instance = s_plugin.GetField("Instance", Any).GetValue(null);
                object addon = s_model.GetField("Addon", Any).GetValue(empty);
                object real = s_loadModel.Invoke(instance, new object[] { addon });
                for (int i = 0; i < Filled.Length; i++)
                {
                    FieldInfo f = s_model.GetField(Filled[i], Any);
                    f.SetValue(empty, f.GetValue(real));
                }
                Say("read " + id + " now, because it is needed: " + (sw.Elapsed.TotalMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " s, memory +" + ((Memory.Commit() - before) / 1048576).ToString(CultureInfo.InvariantCulture) + " MB", false);
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Say("could not read " + id + " (" + inner.GetType().Name + ": " + inner.Message + "); sosigs keep their normal look", true);
            }
            finally
            {
                Call(After);
            }
        }

        private static string SelectedNow()
        {
            try
            {
                object instance = s_plugin.GetField("Instance", Any).GetValue(null);
                if (ReferenceEquals(instance, null)) return "Sosig";
                object entry = s_plugin.GetField("_selectedModel", Any).GetValue(instance);
                if (ReferenceEquals(entry, null)) return "Sosig";
                object value = entry.GetType().GetProperty("Value").GetValue(entry, null);
                return value as string ?? "Sosig";
            }
            catch (Exception)
            {
                // Unknown selection: read everything, as the framework would.
                return "Random";
            }
        }

        private static void Call(Plain hook)
        {
            try
            {
                if (hook != null) hook();
            }
            catch (Exception)
            {
            }
        }

        private static void Say(string text, bool warning)
        {
            Timeline.Write("DEF", "sosig models on demand: " + text);
            if (s_log == null) return;
            if (warning) s_log.LogWarning("Sosig models on demand: " + text);
            else s_log.LogInfo("Sosig models on demand: " + text);
        }
    }
}
