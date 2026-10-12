using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FastLoad
{
    /// <summary>
    /// Talks to Atlas (nrgill28), the mod that registers and loads modded maps, through its public members found at
    /// run time. FastLoad must load and work without Atlas, so there is no compile-time reference.
    /// Uses: AtlasPlugin.CustomSceneInfos, AtlasPlugin.LoadCustomScene(string), AtlasPlugin.CurrentScene,
    /// CustomSceneInfo.Identifier / DisplayName / GameMode / Author.
    /// </summary>
    public static class AtlasBridge
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        private static Type s_plugin;
        private static bool s_looked;

        private static Type Plugin()
        {
            if (!ReferenceEquals(s_plugin, null)) return s_plugin;
            // Atlas is a plugin: it is in memory once BepInEx has loaded plugins. Looked up again until found.
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].GetName().Name != "Atlas") continue;
                Type t = all[i].GetType("Atlas.AtlasPlugin", false);
                if (!ReferenceEquals(t, null)) s_plugin = t;
                break;
            }
            s_looked = true;
            return s_plugin;
        }

        public static bool Found
        {
            get
            {
                try
                {
                    return !ReferenceEquals(Plugin(), null);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Every map Atlas has registered so far. Empty when Atlas is missing or anything goes wrong.</summary>
        public static List<MapEntry> Maps()
        {
            List<MapEntry> maps = new List<MapEntry>();
            try
            {
                Type plugin = Plugin();
                if (ReferenceEquals(plugin, null)) return maps;
                PropertyInfo infos = plugin.GetProperty("CustomSceneInfos", Static);
                if (ReferenceEquals(infos, null)) return maps;
                IEnumerable list = infos.GetValue(null, null) as IEnumerable;
                if (list == null) return maps;
                foreach (object info in list)
                {
                    if (info == null) continue;
                    MapEntry m = new MapEntry();
                    m.Id = Text(info, "Identifier");
                    m.Name = Text(info, "DisplayName");
                    m.Mode = Text(info, "GameMode");
                    m.Author = Text(info, "Author");
                    if (m.Id.Length > 0) maps.Add(m);
                }
            }
            catch (Exception)
            {
            }
            return maps;
        }

        /// <summary>Identifier of the modded map that is loaded now; "" when none or unknown.</summary>
        public static string CurrentId()
        {
            try
            {
                Type plugin = Plugin();
                if (ReferenceEquals(plugin, null)) return "";
                PropertyInfo current = plugin.GetProperty("CurrentScene", Static);
                if (ReferenceEquals(current, null)) return "";
                object info = current.GetValue(null, null);
                return info == null ? "" : Text(info, "Identifier");
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>Asks Atlas to load a map the way its menu panel does. False with a reason when it could not be asked.</summary>
        public static bool Load(string identifier, out string error)
        {
            error = "";
            try
            {
                Type plugin = Plugin();
                if (ReferenceEquals(plugin, null))
                {
                    error = "Atlas is not loaded";
                    return false;
                }
                MethodInfo load = plugin.GetMethod("LoadCustomScene", Static, null, new Type[] { typeof(string) }, null);
                if (ReferenceEquals(load, null))
                {
                    error = "this Atlas version has no LoadCustomScene(string)";
                    return false;
                }
                load.Invoke(null, new object[] { identifier });
                return true;
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private static string Text(object target, string property)
        {
            PropertyInfo p = target.GetType().GetProperty(property, Instance);
            if (ReferenceEquals(p, null)) return "";
            return p.GetValue(target, null) as string ?? "";
        }
    }
}
