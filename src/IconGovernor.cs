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
        /// <summary>When each of those was last on screen (seconds on m_clock); absent = never shown.</summary>
        private readonly Dictionary<int, double> m_shownAt = new Dictionary<int, double>();
        /// <summary>Waiting to be dropped, a few a frame.</summary>
        private readonly Queue<Texture2D> m_queue = new Queue<Texture2D>();
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private long m_queueCommit;
        private int m_queueDropped;
        private string m_queueWhy = "";
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

        private bool m_hooked;
        /// <summary>Texture of each known sprite, by the sprite's instance id.</summary>
        private static readonly Dictionary<int, int> s_textureOfSprite = new Dictionary<int, int>();
        /// <summary>Textures whose pixels are dropped right now, by instance id.</summary>
        private static readonly Dictionary<int, bool> s_dropped = new Dictionary<int, bool>();
        public static int BroughtBack;
        /// <summary>Sprites that had no texture when first met, by instance id.</summary>
        public static readonly Dictionary<int, bool> BornEmpty = new Dictionary<int, bool>();
        /// <summary>Test switch: sprites are handed to images without their picture being read back in.</summary>
        public static bool HookPaused;

        /// <summary>Textures that must never be dropped: nothing on disk is open to read them back from.</summary>
        private static readonly Dictionary<int, bool> s_keep = new Dictionary<int, bool>();
        public static int KeptForClosedFiles;

        private static void UnloadPre(AssetBundle __instance, bool unloadAllLoadedObjects)
        {
            try
            {
                // With "true" the sprites themselves go away with the file: nothing to protect.
                if (unloadAllLoadedObjects || ReferenceEquals(__instance, null) || __instance == null) return;
                // The spawner entries of this file are in memory already (the loader read them), so asking for them
                // costs nothing; their icons are exactly the pictures that are about to lose their file. Icons are
                // often parts of a texture and are not listed as sprites of the file, so asking for sprites is not enough.
                int keptNow = 0, entries = 0;
                string gone = "";
                ItemSpawnerID[] ids = __instance.LoadAllAssets<ItemSpawnerID>();
                for (int i = 0; ids != null && i < ids.Length; i++)
                {
                    if (ids[i] == null) continue;
                    entries++;
                    Protect(ids[i].Sprite, ref keptNow, ref gone);
                }
                IconGovernor me = Instance;
                if (me != null && !ReferenceEquals(me.m_entryIcon, null))
                {
                    UnityEngine.Object[] more = __instance.LoadAllAssets(me.m_entryType);
                    for (int i = 0; more != null && i < more.Length; i++)
                    {
                        if (more[i] == null) continue;
                        entries++;
                        Protect(me.m_entryIcon.GetValue(more[i]) as Sprite, ref keptNow, ref gone);
                    }
                }
                Sprite[] sprites = __instance.LoadAllAssets<Sprite>();
                for (int i = 0; sprites != null && i < sprites.Length; i++) Protect(sprites[i], ref keptNow, ref gone);
                // And by name: an icon that is not reached through an entry of this file but is one of its files
                // ("backpack" from ".../backpack.png"). A same-named icon of another mod gets kept too; that only costs memory.
                if (me != null)
                {
                    string[] assetNames = __instance.GetAllAssetNames();
                    Dictionary<string, bool> files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; assetNames != null && i < assetNames.Length; i++)
                    {
                        string file = System.IO.Path.GetFileNameWithoutExtension(assetNames[i] ?? "");
                        if (file.Length > 0) files[file] = true;
                    }
                    if (files.Count > 0)
                    {
                        foreach (Sprite known in me.m_known.Values)
                        {
                            if (ReferenceEquals(known, null) || known == null) continue;
                            if (files.ContainsKey(known.name)) Protect(known, ref keptNow, ref gone);
                        }
                    }
                }
                Timeline.Write("ICO", "file '" + __instance.name + "' is closing with its contents kept: " + entries + " spawner entries, " + (sprites != null ? sprites.Length : 0)
                    + " sprites, " + keptNow + " pictures now protected" + (gone.Length > 0 ? ", NO PICTURE for:" + gone : ""));
            }
            catch (Exception)
            {
            }
        }

        private static void Protect(Sprite sprite, ref int keptNow, ref string gone)
        {
            if (ReferenceEquals(sprite, null) || sprite == null) return;
            // The file is still open here: a dropped picture comes back with this.
            Texture2D t = sprite.texture;
            if (ReferenceEquals(t, null) || t == null)
            {
                gone += " '" + sprite.name + "'";
                return;
            }
            int tid = t.GetInstanceID();
            if (s_keep.ContainsKey(tid)) return;
            s_keep[tid] = true;
            s_dropped.Remove(tid);
            KeptForClosedFiles++;
            keptNow++;
            IconGovernor g = Instance;
            if (g != null) g.m_loaded.Remove(tid);
        }

        private static void SpritePre(Sprite value)
        {
            try
            {
                if (HookPaused || ReferenceEquals(value, null) || s_dropped.Count == 0) return;
                int tid;
                if (!s_textureOfSprite.TryGetValue(value.GetInstanceID(), out tid) || !s_dropped.ContainsKey(tid)) return;
                s_dropped.Remove(tid);
                // Asking for it is what reads it back in.
                Texture2D t = value.texture;
                BroughtBack++;
                IconGovernor g = Instance;
                if (g != null && !ReferenceEquals(t, null) && t != null)
                {
                    g.m_loaded[tid] = t;
                    g.m_shownAt[tid] = g.m_clock.Elapsed.TotalSeconds;
                }
            }
            catch (Exception)
            {
            }
        }

        public int Known { get { return m_known.Count; } }
        public long Dropped { get { return m_dropped; } }

        private void Awake()
        {
            Instance = this;
            // A dropped picture does not come back by being drawn (measured: a material or UI image keeps showing
            // nothing until it is given the texture again). So: the moment any UI image is handed one of these
            // sprites, its picture is read back in, before the image looks at it.
            try
            {
                MethodInfo set = typeof(Image).GetProperty("sprite").GetSetMethod();
                new HarmonyLib.Harmony("invent60.h3vr.fastload.icons").Patch(set, new HarmonyLib.HarmonyMethod(typeof(IconGovernor).GetMethod("SpritePre", BindingFlags.NonPublic | BindingFlags.Static)));
                // A mod file that is closed while its contents stay in memory (OtherLoader does this for 29 older mods
                // on the test profile) leaves pictures that can never be read back: dropping one loses it for good and
                // its icon turns into a white square. Seen: sprite 'Badge ftw', texture gone. So just before such a
                // file closes, every sprite in it is marked "keep", and read back in first if it was dropped already.
                MethodInfo unload = typeof(AssetBundle).GetMethod("Unload", new Type[] { typeof(bool) });
                new HarmonyLib.Harmony("invent60.h3vr.fastload.icons.keep").Patch(unload, new HarmonyLib.HarmonyMethod(typeof(IconGovernor).GetMethod("UnloadPre", BindingFlags.NonPublic | BindingFlags.Static)));
                m_hooked = true;
            }
            catch (Exception)
            {
                // Without the hook nothing may be dropped: Update checks m_hooked.
                m_hooked = false;
            }
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
                if (!m_hooked) return;
                if (m_queue.Count > 0)
                {
                    Drain();
                    return;
                }
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
                if (!ReferenceEquals(id, null) && id != null && Meet(id.Sprite, id.GetInstanceID() > 0)) fresh++;
            }
            if (!m_entryLooked) FindEntryType();
            if (!ReferenceEquals(m_entryIcon, null))
            {
                UnityEngine.Object[] entries = Resources.FindObjectsOfTypeAll(m_entryType);
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] != null && Meet(m_entryIcon.GetValue(entries[i]) as Sprite, entries[i].GetInstanceID() > 0)) fresh++;
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
        /// <summary>Sprites whose picture may be dropped only once an owner that is a file's own asset has been seen.</summary>
        private readonly Dictionary<int, bool> m_waitingForAssetOwner = new Dictionary<int, bool>();

        /// <summary>
        /// True when this sprite's picture has just become droppable.
        /// ownerIsAsset: the spawner entry holding it came out of a mod file (positive instance id). An entry made
        /// while the game runs (loaders build some from older data) says nothing about where its icon's file is or
        /// whether it is still open, and only entries from files are covered by the "file is closing" protection.
        /// An icon held only by such entries is never dropped. Seen: sprite 'backpack', lost for good before this rule.
        /// </summary>
        private bool Meet(Sprite sprite, bool ownerIsAsset)
        {
            if (ReferenceEquals(sprite, null) || sprite == null) return false;
            int id = sprite.GetInstanceID();
            if (m_known.ContainsKey(id))
            {
                if (!ownerIsAsset || !m_waitingForAssetOwner.ContainsKey(id)) return false;
                m_waitingForAssetOwner.Remove(id);
                int known;
                if (!s_textureOfSprite.TryGetValue(id, out known) || s_keep.ContainsKey(known)) return false;
                // Never dropped so far, so asking reads nothing.
                Texture2D now = sprite.texture;
                if (ReferenceEquals(now, null) || now == null) return false;
                m_loaded[known] = now;
                return true;
            }
            m_known[id] = sprite;
            Texture2D t = sprite.texture;
            if (ReferenceEquals(t, null) || t == null)
            {
                // Came without a picture: nothing of ours.
                BornEmpty[id] = true;
                return false;
            }
            int tid = t.GetInstanceID();
            if (tid < 0)
            {
                // Made while the game runs: nothing on disk to read it back from.
                m_runtimeMade++;
                return false;
            }
            s_textureOfSprite[id] = tid;
            if (s_keep.ContainsKey(tid)) return false;
            if (!ownerIsAsset)
            {
                m_waitingForAssetOwner[id] = true;
                return false;
            }
            m_loaded[tid] = t;
            return true;
        }

        /// <summary>
        /// Instance ids of the icon textures some UI image or sprite renderer holds right now, shown or hidden.
        /// Hidden ones count: an image that keeps its sprite while its panel is switched off would come back as a
        /// blank square if the picture were dropped meanwhile (it is only read back in when a sprite is handed over).
        /// </summary>
        private Dictionary<int, bool> Shown()
        {
            Dictionary<int, bool> shown = new Dictionary<int, bool>();
            if (m_known.Count == 0) return shown;
            UnityEngine.Object[] images = Resources.FindObjectsOfTypeAll(typeof(Image));
            for (int i = 0; i < images.Length; i++)
            {
                Image img = images[i] as Image;
                if (!ReferenceEquals(img, null) && img != null) See(img.sprite, shown);
            }
            UnityEngine.Object[] renderers = Resources.FindObjectsOfTypeAll(typeof(SpriteRenderer));
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer sr = renderers[i] as SpriteRenderer;
                if (!ReferenceEquals(sr, null) && sr != null) See(sr.sprite, shown);
            }
            return shown;
        }

        private void See(Sprite sprite, Dictionary<int, bool> shown)
        {
            if (ReferenceEquals(sprite, null) || sprite == null) return;
            int tid;
            // By the id noted when the sprite was first met: asking the sprite for its texture would read it back in.
            if (!s_textureOfSprite.TryGetValue(sprite.GetInstanceID(), out tid)) return;
            shown[tid] = true;
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
            double now = m_clock.Elapsed.TotalSeconds;
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, Texture2D> pair in m_loaded)
            {
                if (shown.ContainsKey(pair.Key))
                {
                    m_shownAt[pair.Key] = now;
                    continue;
                }
                double at;
                if (m_shownAt.TryGetValue(pair.Key, out at))
                {
                    // Was on screen: stays until it has been off screen long enough, or for good with unloading off.
                    if (!Unloading.Enabled || now - at < Unloading.IdleSeconds) continue;
                    m_shownAt.Remove(pair.Key);
                }
                gone.Add(pair.Key);
                // Equal to null = dropped already (or destroyed); nothing to do.
                if (pair.Value != null) m_queue.Enqueue(pair.Value);
            }
            for (int i = 0; i < gone.Count; i++) m_loaded.Remove(gone[i]);
            if (m_queue.Count == 0) return "nothing to drop";
            m_queueCommit = Memory.Commit();
            m_queueDropped = 0;
            m_queueWhy = why + "; heldOnlyByRunTimeEntries=" + m_waitingForAssetOwner.Count + " new=" + fresh + " scan=" + (int)scanMs + "ms known=" + m_known.Count + " onScreen=" + shown.Count + " madeAtRunTime=" + m_runtimeMade;
            return "queued " + m_queue.Count + " item pictures to drop";
        }

        /// <summary>Drops what is queued, at most Unloading.FrameBudgetMs of it a frame.</summary>
        private void Drain()
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (m_queue.Count > 0 && sw.Elapsed.TotalMilliseconds < Unloading.FrameBudgetMs)
            {
                Texture2D t = m_queue.Dequeue();
                if (t == null || s_keep.ContainsKey(t.GetInstanceID())) continue;
                s_dropped[t.GetInstanceID()] = true;
                Resources.UnloadAsset(t);
                m_queueDropped++;
            }
            if (m_queue.Count > 0) return;
            m_dropped += m_queueDropped;
            long freed = m_queueCommit - Memory.Commit();
            // Probe file only: unloading is not something the player is told about.
            Timeline.Write("ICO", "dropped " + m_queueDropped + " item pictures (" + m_queueWhy + "): memory " + (freed >= 0 ? "-" : "+") + (Math.Abs(freed) / 1048576)
                + " MB; droppedSoFar=" + m_dropped);
        }
    }
}
