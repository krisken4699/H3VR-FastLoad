using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FastLoad
{
    /// <summary>
    /// "[OnDemand] ItemIcons": every item in the item spawner has a picture, and the mod loaders read all of those
    /// pictures into memory while they load the item lists: 9263 textures and about 16 GB of the game's memory on
    /// the profile this was measured on, most of them uncompressed, whether or not the spawner is ever opened.
    ///
    /// Here their pixels are dropped again as the lists come in (Resources.UnloadAsset). Unity reads a dropped
    /// picture back from its mod file by itself the moment something draws or touches it, so the spawner shows
    /// them as always; pictures that were shown are dropped again once they are off screen.
    ///
    /// Rules this relies on, all seen in the game:
    ///  - a texture made at run time (negative instance id) has no file to come back from and is never dropped;
    ///  - after a drop, the old Texture2D reference compares equal to null for good, and asking a sprite for its
    ///    texture reads the picture back in. So sprites are remembered, never textures, and a sprite is only asked
    ///    for its texture when new or when it is on screen (loaded anyway).
    /// </summary>
    public sealed class IconGovernor : MonoBehaviour
    {
        public ManualLogSource Log;

        private const double ScanWhileLoadingSeconds = 3.0;
        private const double ScanLaterSeconds = 120.0;
        private const double LookSeconds = 1.0;

        public static IconGovernor Instance;

        private readonly Dictionary<int, Sprite> m_known = new Dictionary<int, Sprite>();
        /// <summary>Textures that hold pixels as far as this knows, by instance id.</summary>
        private readonly Dictionary<int, Texture2D> m_loaded = new Dictionary<int, Texture2D>();
        private readonly Stopwatch m_sinceScan = new Stopwatch();
        private readonly Stopwatch m_sinceLook = new Stopwatch();
        private ItemsLoadedWatch m_items;
        private bool m_itemsDone;
        private bool m_sceneCame;
        private Type m_entryType;
        private FieldInfo m_entryIcon;
        private bool m_entryLooked;
        private int m_runtimeMade;
        private long m_dropped;

        public int Known { get { return m_known.Count; } }
        public long Dropped { get { return m_dropped; } }

        private void Awake()
        {
            Instance = this;
            m_items = new ItemsLoadedWatch();
            BepInEx.Logging.Logger.Listeners.Add(m_items);
            SceneManager.sceneLoaded += OnSceneLoaded;
            m_sinceScan.Start();
            m_sinceLook.Start();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive) m_sceneCame = true;
        }

        private void Update()
        {
            try
            {
                bool itemsNow = m_items.Seen && !m_itemsDone;
                double scanEvery = m_itemsDone ? ScanLaterSeconds : ScanWhileLoadingSeconds;
                if (itemsNow || m_sceneCame || m_sinceScan.Elapsed.TotalSeconds >= scanEvery)
                {
                    if (itemsNow) m_itemsDone = true;
                    m_sceneCame = false;
                    m_sinceScan.Reset();
                    m_sinceScan.Start();
                    Pass(itemsNow ? "all items loaded" : "scan");
                }
                else if (m_sinceLook.Elapsed.TotalSeconds >= LookSeconds)
                {
                    m_sinceLook.Reset();
                    m_sinceLook.Start();
                    // Cheap: only pictures that were on screen can be holding pixels.
                    if (m_itemsDone) Sweep(Shown(), "off screen", 0, 0);
                }
            }
            catch (Exception e)
            {
                enabled = false;
                Log.LogError("Item icons on demand switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Finds pictures not seen before and drops everything that is not on screen.</summary>
        public string Pass(string why)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int fresh = Scan();
            double scanMs = sw.Elapsed.TotalMilliseconds;
            return Sweep(Shown(), why, fresh, scanMs);
        }

        private int Scan()
        {
            int fresh = 0;
            UnityEngine.Object[] ids = Resources.FindObjectsOfTypeAll(typeof(ItemSpawnerID));
            for (int i = 0; i < ids.Length; i++)
            {
                ItemSpawnerID id = ids[i] as ItemSpawnerID;
                if (!ReferenceEquals(id, null) && id != null && Meet(id.Sprite)) fresh++;
            }
            if (!m_entryLooked) FindEntryType();
            if (!ReferenceEquals(m_entryIcon, null))
            {
                UnityEngine.Object[] entries = Resources.FindObjectsOfTypeAll(m_entryType);
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null && Meet(m_entryIcon.GetValue(entries[i]) as Sprite)) fresh++;
                }
            }
            return fresh;
        }

        /// <summary>OtherLoader's own spawner entry type, by name; looked for again until its assembly is there.</summary>
        private void FindEntryType()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    if (a.GetName().Name.StartsWith("OtherLoader", StringComparison.Ordinal)) t = a.GetType("OtherLoader.ItemSpawnerEntry");
                }
                catch (Exception)
                {
                }
                if (ReferenceEquals(t, null)) continue;
                m_entryType = t;
                m_entryIcon = t.GetField("EntryIcon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                m_entryLooked = true;
                return;
            }
        }

        /// <summary>True when this sprite is new and its picture can be dropped.</summary>
        private bool Meet(Sprite sprite)
        {
            if (ReferenceEquals(sprite, null) || sprite == null) return false;
            int id = sprite.GetInstanceID();
            if (m_known.ContainsKey(id)) return false;
            m_known[id] = sprite;
            Texture2D t = sprite.texture;
            if (ReferenceEquals(t, null) || t == null) return false;
            int tid = t.GetInstanceID();
            if (tid < 0)
            {
                // Made while the game runs: nothing on disk to read it back from.
                m_runtimeMade++;
                return false;
            }
            m_loaded[tid] = t;
            return true;
        }

        /// <summary>Instance ids of the icon textures something on screen is showing right now.</summary>
        private Dictionary<int, bool> Shown()
        {
            Dictionary<int, bool> shown = new Dictionary<int, bool>();
            if (m_known.Count == 0) return shown;
            Image[] images = FindObjectsOfType<Image>();
            for (int i = 0; i < images.Length; i++) See(images[i].sprite, shown);
            SpriteRenderer[] renderers = FindObjectsOfType<SpriteRenderer>();
            for (int i = 0; i < renderers.Length; i++) See(renderers[i].sprite, shown);
            return shown;
        }

        private void See(Sprite sprite, Dictionary<int, bool> shown)
        {
            if (ReferenceEquals(sprite, null) || sprite == null || !m_known.ContainsKey(sprite.GetInstanceID())) return;
            // On screen, so its pixels are in memory already: asking costs nothing.
            Texture2D t = sprite.texture;
            if (ReferenceEquals(t, null) || t == null) return;
            int tid = t.GetInstanceID();
            if (tid < 0) return;
            shown[tid] = true;
            m_loaded[tid] = t;
        }

        /// <summary>A picture something outside this class read back in (tests).</summary>
        public void Touched(Texture2D t)
        {
            if (ReferenceEquals(t, null) || t == null || t.GetInstanceID() < 0) return;
            m_loaded[t.GetInstanceID()] = t;
        }

        public List<Sprite> KnownSprites()
        {
            return new List<Sprite>(m_known.Values);
        }

        private string Sweep(Dictionary<int, bool> shown, string why, int fresh, double scanMs)
        {
            if (m_loaded.Count == 0) return "nothing to drop";
            long before = Memory.Commit();
            Stopwatch sw = Stopwatch.StartNew();
            int dropped = 0;
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, Texture2D> pair in m_loaded)
            {
                if (shown.ContainsKey(pair.Key)) continue;
                gone.Add(pair.Key);
                // Equal to null = dropped already (or destroyed); nothing to do.
                if (pair.Value == null) continue;
                Resources.UnloadAsset(pair.Value);
                dropped++;
            }
            for (int i = 0; i < gone.Count; i++) m_loaded.Remove(gone[i]);
            if (dropped == 0) return "nothing to drop";
            m_dropped += dropped;
            long freed = before - Memory.Commit();
            string text = "dropped " + dropped + " item pictures (" + why + "): memory " + (freed >= 0 ? "-" : "+") + (Math.Abs(freed) / 1048576) + " MB in "
                + (int)sw.Elapsed.TotalMilliseconds + " ms; new=" + fresh + " scan=" + (int)scanMs + "ms known=" + m_known.Count + " onScreen=" + shown.Count
                + " madeAtRunTime=" + m_runtimeMade + " droppedSoFar=" + m_dropped;
            Timeline.Write("ICO", text);
            if (dropped >= 200 || why == "all items loaded") Log.LogInfo("Item icons on demand: " + text);
            return text;
        }
    }
}
