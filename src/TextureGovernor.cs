using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using FastLoad.Early;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FastLoad
{
    /// <summary>"[Unload]" settings, shared by the governors.</summary>
    public static class Unloading
    {
        /// <summary>False: what was in use once stays in memory. Things that were loaded and never used are still dropped.</summary>
        public static bool Enabled = true;
        /// <summary>How long something has to be out of the scene before it is dropped.</summary>
        public static double IdleSeconds = 120.0;
        /// <summary>Main-thread time a frame the governors may spend dropping or looking, milliseconds.</summary>
        public const double FrameBudgetMs = 1.5;
    }

    /// <summary>
    /// "[OnDemand] Textures" and "[Unload]": textures nothing in the scene uses are dropped (Resources.UnloadAsset);
    /// Unity reads a dropped texture back from its still-open mod file by itself when something draws it.
    ///
    /// Two cases:
    ///  - loaded and never used (a loader read a whole mod file for one thing in it): dropped a few seconds after
    ///    it came in. Spawning one modular rifle brought in 8600 such textures, +31 GB.
    ///  - used, then gone from the scene (a gun that was spawned and deleted): dropped after [Unload] IdleSeconds,
    ///    when [Unload] Enabled.
    ///
    /// Left alone: what was there at the first look (the game's own), a map's own textures while that map is
    /// loaded, textures made at run time (negative instance id: nothing on disk to come back from), textures that
    /// came straight back after a drop (something uses them in a way this cannot see), and textures Unity would
    /// not drop. A wrong guess costs one re-read of that texture, never a missing texture.
    ///
    /// Never more than Unloading.FrameBudgetMs of work in a frame: looking through the scene and dropping are
    /// both spread over as many frames as they take.
    /// </summary>
    public sealed class TextureGovernor : MonoBehaviour
    {
        public ManualLogSource Log;

        private const double BusyScanSeconds = 2.0;
        private const double QuietScanSeconds = 10.0;
        /// <summary>Never-used textures wait this long: what is being spawned gets time to appear.</summary>
        private const double GraceSeconds = 4.0;
        private const double SceneGraceSeconds = 15.0;
        private const double HotSeconds = 20.0;
        private const int MaxDrops = 3;

        private enum Hold { None, Game, Scene, Hot, Fixed }

        private sealed class Seen
        {
            public double First;
            public double LastUsed = -1;
            public double DroppedAt = -1;
            public int Drops;
            public Hold Hold;
        }

        private static readonly string[] Slots = new string[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_EmissionMap",
            "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask", "_ParallaxMap", "_SpecTex", "_MaterialMap", "_PackedMap", "_BumpMap2", "_MainTex2", "_MaterialMap2",
            "_EmissionMap2", "_DecalTex", "_Illum", "_Detail", "_IncandescenceMap", "_RimTex", "_Rma", "_MaskTex", "_NormalMap", "_AlloyPM", "_BaseColor" };
        private int[] m_slotIds;

        private readonly Dictionary<int, Seen> m_seen = new Dictionary<int, Seen>();
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly Stopwatch m_sinceScan = Stopwatch.StartNew();
        private double m_lastSceneAt = -1000;
        private double m_lastLoadAt = -1000;
        private bool m_first = true;
        private bool m_sceneChanged;

        // One pass, spread over frames: list -> walk the scene -> drop.
        private enum Phase { Idle, Walk, Drop }
        private Phase m_phase = Phase.Idle;
        private List<Texture2D> m_ripe;
        private Renderer[] m_renderers;
        private Graphic[] m_graphics;
        private int m_walkAt;
        private int m_dropAt;
        private Dictionary<int, bool> m_used;
        private Dictionary<int, bool> m_doneMaterials;
        private int m_passDropped, m_passInScene, m_passRefused, m_passFrames;
        private long m_passCommit;
        private double m_passWorstMs;
        private long m_dropped, m_hot, m_refused;

        private void Awake()
        {
            m_slotIds = new int[Slots.Length];
            for (int i = 0; i < Slots.Length; i++) m_slotIds[i] = Shader.PropertyToID(Slots[i]);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Additive) return;
            m_lastSceneAt = m_clock.Elapsed.TotalSeconds;
            m_sceneChanged = true;
        }

        private void Update()
        {
            try
            {
                double now = m_clock.Elapsed.TotalSeconds;
                string ignored;
                if (LoadFeed.Line(Clock.NowMs, 0, out ignored)) m_lastLoadAt = now;
                // No scene yet, or one on its way: nothing is settled.
                if (m_lastSceneAt < 0 || StartScene.Busy || SteamVR_LoadLevel.loading) return;

                Stopwatch sw = Stopwatch.StartNew();
                if (m_phase == Phase.Idle)
                {
                    double every = now - m_lastLoadAt < 12.0 ? BusyScanSeconds : QuietScanSeconds;
                    if (m_sinceScan.Elapsed.TotalSeconds < every) return;
                    m_sinceScan.Reset();
                    m_sinceScan.Start();
                    List(now);
                }
                else if (m_phase == Phase.Walk) Walk(sw);
                else Drop(sw, now);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > m_passWorstMs) m_passWorstMs = ms;
                m_passFrames++;
            }
            catch (Exception e)
            {
                enabled = false;
                Log.LogError("Textures on demand switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>What is loaded now (this lists, it does not load), and which of it is due.</summary>
        private void List(double now)
        {
            bool sceneFresh = now - m_lastSceneAt < SceneGraceSeconds;
            if (m_sceneChanged)
            {
                // The last map's own textures and the "something uses it" guesses start over.
                m_sceneChanged = false;
                foreach (Seen s in m_seen.Values)
                {
                    if (s.Hold != Hold.Scene && s.Hold != Hold.Hot) continue;
                    s.Hold = Hold.None;
                    s.LastUsed = now;
                    s.DroppedAt = -1;
                }
            }
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(Texture2D));
            List<Texture2D> ripe = null;
            for (int i = 0; i < all.Length; i++)
            {
                Texture2D t = all[i] as Texture2D;
                if (ReferenceEquals(t, null)) continue;
                int id = t.GetInstanceID();
                if (id < 0) continue;
                Seen s;
                if (!m_seen.TryGetValue(id, out s))
                {
                    s = new Seen();
                    s.First = now;
                    s.Hold = m_first ? Hold.Game : sceneFresh ? Hold.Scene : Hold.None;
                    m_seen[id] = s;
                    continue;
                }
                if (s.Hold != Hold.None) continue;
                if (s.DroppedAt >= 0)
                {
                    // It is back.
                    if (now - s.DroppedAt < HotSeconds || s.Drops >= MaxDrops)
                    {
                        s.Hold = s.Drops >= MaxDrops ? Hold.Fixed : Hold.Hot;
                        m_hot++;
                        continue;
                    }
                    s.DroppedAt = -1;
                    s.First = now;
                    s.LastUsed = -1;
                    continue;
                }
                bool due = s.LastUsed < 0 ? now - s.First >= GraceSeconds : Unloading.Enabled && now - s.LastUsed >= Unloading.IdleSeconds;
                if (!due) continue;
                if (ripe == null) ripe = new List<Texture2D>();
                ripe.Add(t);
            }
            m_first = false;
            m_passFrames = 0;
            m_passWorstMs = 0;
            if (ripe == null) return;

            m_ripe = ripe;
            // Everything in the scene, switched on or not: a muzzle flash that is off now must not hitch when it fires.
            m_renderers = Resources.FindObjectsOfTypeAll(typeof(Renderer)) as Renderer[];
            if (m_renderers == null)
            {
                UnityEngine.Object[] raw = Resources.FindObjectsOfTypeAll(typeof(Renderer));
                m_renderers = new Renderer[raw.Length];
                for (int i = 0; i < raw.Length; i++) m_renderers[i] = raw[i] as Renderer;
            }
            m_graphics = FindObjectsOfType<Graphic>();
            m_used = new Dictionary<int, bool>();
            m_doneMaterials = new Dictionary<int, bool>();
            m_walkAt = 0;
            m_dropAt = 0;
            m_passDropped = 0;
            m_passInScene = 0;
            m_passRefused = 0;
            m_passCommit = Memory.Commit();
            m_phase = Phase.Walk;
        }

        private void Walk(Stopwatch sw)
        {
            int total = m_renderers.Length + m_graphics.Length;
            while (m_walkAt < total)
            {
                if ((m_walkAt & 15) == 0 && sw.Elapsed.TotalMilliseconds >= Unloading.FrameBudgetMs) return;
                int i = m_walkAt++;
                if (i < m_renderers.Length)
                {
                    Renderer r = m_renderers[i];
                    // Gone since the list was made, or a prefab that is not in any scene.
                    if (ReferenceEquals(r, null) || r == null || !r.gameObject.scene.IsValid()) continue;
                    Material[] mats = r.sharedMaterials;
                    for (int m = 0; m < mats.Length; m++) Take(mats[m]);
                }
                else
                {
                    Graphic g = m_graphics[i - m_renderers.Length];
                    if (ReferenceEquals(g, null) || g == null) continue;
                    Texture t = g.mainTexture;
                    if (!ReferenceEquals(t, null) && t != null) m_used[t.GetInstanceID()] = true;
                    Take(g.material);
                }
            }
            LightmapData[] maps = LightmapSettings.lightmaps;
            for (int i = 0; maps != null && i < maps.Length; i++)
            {
                if (maps[i] == null) continue;
                if (maps[i].lightmapColor != null) m_used[maps[i].lightmapColor.GetInstanceID()] = true;
                if (maps[i].lightmapDir != null) m_used[maps[i].lightmapDir.GetInstanceID()] = true;
            }
            Take(RenderSettings.skybox);
            m_renderers = null;
            m_graphics = null;
            m_doneMaterials = null;
            m_phase = Phase.Drop;
        }

        private void Take(Material mat)
        {
            if (ReferenceEquals(mat, null) || mat == null) return;
            int mid = mat.GetInstanceID();
            if (m_doneMaterials.ContainsKey(mid)) return;
            m_doneMaterials[mid] = true;
            for (int i = 0; i < m_slotIds.Length; i++)
            {
                if (!mat.HasProperty(m_slotIds[i])) continue;
                Texture t = mat.GetTexture(m_slotIds[i]);
                if (!ReferenceEquals(t, null) && t != null) m_used[t.GetInstanceID()] = true;
            }
        }

        private void Drop(Stopwatch sw, double now)
        {
            while (m_dropAt < m_ripe.Count)
            {
                if (sw.Elapsed.TotalMilliseconds >= Unloading.FrameBudgetMs) return;
                Texture2D t = m_ripe[m_dropAt++];
                // Dropped or destroyed by someone else in the meantime.
                if (t == null) continue;
                int id = t.GetInstanceID();
                Seen s = m_seen[id];
                if (m_used.ContainsKey(id))
                {
                    s.LastUsed = now;
                    m_passInScene++;
                    continue;
                }
                Resources.UnloadAsset(t);
                if (t != null)
                {
                    // Still there: Unity has no file to read it back from (its bundle was closed), so it stays.
                    s.Hold = Hold.Fixed;
                    m_passRefused++;
                    continue;
                }
                s.DroppedAt = now;
                s.Drops++;
                m_passDropped++;
            }
            m_phase = Phase.Idle;
            m_ripe = null;
            m_used = null;
            m_dropped += m_passDropped;
            m_refused += m_passRefused;
            if (m_passDropped == 0 && m_passRefused == 0) return;
            long freed = m_passCommit - Memory.Commit();
            // Probe file only: unloading is not something the player is told about.
            Timeline.Write("TEX", "dropped " + m_passDropped + " textures nothing in the scene uses: memory " + (freed >= 0 ? "-" : "+") + (Math.Abs(freed) / 1048576)
                + " MB over " + m_passFrames + " frames, most in one frame " + m_passWorstMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms; inScene="
                + m_passInScene + " notDroppable=" + m_passRefused + " | so far dropped=" + m_dropped + " cameBackAndKept=" + m_hot + " notDroppable=" + m_refused);
        }
    }
}
