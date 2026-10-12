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
    /// <summary>
    /// "[OnDemand] Textures": mod loaders read whole asset bundles to get at a few things in them. Spawning one
    /// modular rifle made ModulWorkshop_Optimizer load 52 part bundles in full: 8600 textures, +31 GB, for the
    /// handful of parts on the gun. The textures of everything not in the scene are dropped here again
    /// (Resources.UnloadAsset); Unity reads a dropped texture back from its still-open bundle by itself when
    /// something draws it.
    ///
    /// What is left alone:
    ///  - textures that were there before the first scan, or that came in within SceneGraceSeconds of a scene load
    ///    (the scene's own);
    ///  - textures made at run time (negative instance id): nothing on disk to come back from;
    ///  - textures a renderer or UI element in the scene uses, as far as the usual material slots show;
    ///  - "hot" textures: dropped, and back in memory within HotSeconds, so something does use them;
    ///  - textures that were dropped MaxDrops times already, or that Unity would not drop (their bundle is closed).
    /// A wrong guess costs one re-read of that texture, never a missing texture.
    /// </summary>
    public sealed class TextureGovernor : MonoBehaviour
    {
        public ManualLogSource Log;

        private const double ScanSeconds = 2.0;
        /// <summary>A texture has to be seen loaded this long before it can be dropped: what is being spawned gets time to appear.</summary>
        private const double GraceSeconds = 4.0;
        private const double SceneGraceSeconds = 15.0;
        private const double HotSeconds = 20.0;
        private const int MaxDrops = 3;
        /// <summary>Drops in one frame; more wait for the next scan.</summary>
        private const int DropsAFrame = 1500;

        private sealed class Seen
        {
            public double First;
            public double DroppedAt = -1;
            public int Drops;
            public bool Keep;
        }

        private static readonly string[] Slots = new string[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_EmissionMap",
            "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask", "_ParallaxMap", "_SpecTex", "_MaterialMap", "_PackedMap", "_BumpMap2", "_MainTex2", "_MaterialMap2",
            "_EmissionMap2", "_DecalTex", "_Illum", "_Detail", "_IncandescenceMap", "_RimTex", "_Rma", "_MaskTex", "_NormalMap", "_AlloyPM", "_BaseColor" };
        private int[] m_slotIds;

        private readonly Dictionary<int, Seen> m_seen = new Dictionary<int, Seen>();
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly Stopwatch m_sinceScan = Stopwatch.StartNew();
        private double m_lastSceneAt = -1000;
        private bool m_first = true;
        private long m_dropped;
        private long m_hot;
        private long m_refused;

        private void Awake()
        {
            m_slotIds = new int[Slots.Length];
            for (int i = 0; i < Slots.Length; i++) m_slotIds[i] = Shader.PropertyToID(Slots[i]);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive) m_lastSceneAt = m_clock.Elapsed.TotalSeconds;
        }

        private void Update()
        {
            if (m_sinceScan.Elapsed.TotalSeconds < ScanSeconds) return;
            m_sinceScan.Reset();
            m_sinceScan.Start();
            try
            {
                Scan();
            }
            catch (Exception e)
            {
                enabled = false;
                Log.LogError("Textures on demand switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Scan()
        {
            // No scene yet, or one on its way: nothing is settled.
            if (m_lastSceneAt < 0 || StartScene.Busy || SteamVR_LoadLevel.loading) return;
            double now = m_clock.Elapsed.TotalSeconds;
            bool sceneFresh = now - m_lastSceneAt < SceneGraceSeconds;
            Stopwatch sw = Stopwatch.StartNew();

            // Lists what is loaded; does not load anything.
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
                    s.Keep = m_first || sceneFresh;
                    m_seen[id] = s;
                    continue;
                }
                if (s.Keep) continue;
                if (s.DroppedAt >= 0)
                {
                    // It is back.
                    if (now - s.DroppedAt < HotSeconds || s.Drops >= MaxDrops)
                    {
                        s.Keep = true;
                        m_hot++;
                        continue;
                    }
                    s.DroppedAt = -1;
                    s.First = now;
                    continue;
                }
                if (now - s.First < GraceSeconds) continue;
                if (ripe == null) ripe = new List<Texture2D>();
                ripe.Add(t);
            }
            m_first = false;
            if (ripe == null) return;

            double listMs = sw.Elapsed.TotalMilliseconds;
            Dictionary<int, bool> used = InUse();
            double useMs = sw.Elapsed.TotalMilliseconds - listMs;
            long before = Memory.Commit();
            int dropped = 0;
            int kept = 0;
            int refused = 0;
            for (int i = 0; i < ripe.Count && dropped < DropsAFrame; i++)
            {
                Texture2D t = ripe[i];
                int id = t.GetInstanceID();
                Seen s = m_seen[id];
                if (used.ContainsKey(id))
                {
                    // In the scene now. Looked at again later, when it may be gone from it.
                    s.First = now;
                    kept++;
                    continue;
                }
                Resources.UnloadAsset(t);
                if (t != null)
                {
                    // Still there: Unity has no file to read it back from (its bundle was closed), so it stays for good.
                    s.Keep = true;
                    refused++;
                    continue;
                }
                s.DroppedAt = now;
                s.Drops++;
                dropped++;
            }
            m_dropped += dropped;
            m_refused += refused;
            if (dropped == 0 && refused == 0) return;
            long freed = before - Memory.Commit();
            string text = "dropped " + dropped + " textures nothing in the scene uses: memory " + (freed >= 0 ? "-" : "+") + (Math.Abs(freed) / 1048576) + " MB; ripe=" + ripe.Count
                + " inScene=" + kept + " notDroppable=" + refused + " loaded=" + all.Length + " list=" + (int)listMs + "ms use=" + (int)useMs + "ms total="
                + (int)sw.Elapsed.TotalMilliseconds + "ms | so far dropped=" + m_dropped + " cameBackAndKept=" + m_hot + " notDroppable=" + m_refused;
            Timeline.Write("TEX", text);
            if (dropped >= 300) Log.LogInfo("Textures on demand: " + text);
        }

        /// <summary>Instance ids of the textures the scene's renderers and UI show, as far as the usual material slots tell.</summary>
        private Dictionary<int, bool> InUse()
        {
            Dictionary<int, bool> used = new Dictionary<int, bool>();
            Dictionary<int, bool> doneMaterials = new Dictionary<int, bool>();
            Renderer[] renderers = FindObjectsOfType<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] mats = renderers[i].sharedMaterials;
                for (int m = 0; m < mats.Length; m++) Take(mats[m], used, doneMaterials);
            }
            Graphic[] graphics = FindObjectsOfType<Graphic>();
            for (int i = 0; i < graphics.Length; i++)
            {
                Texture t = graphics[i].mainTexture;
                if (!ReferenceEquals(t, null) && t != null) used[t.GetInstanceID()] = true;
                Take(graphics[i].material, used, doneMaterials);
            }
            LightmapData[] maps = LightmapSettings.lightmaps;
            for (int i = 0; maps != null && i < maps.Length; i++)
            {
                if (maps[i] == null) continue;
                if (maps[i].lightmapColor != null) used[maps[i].lightmapColor.GetInstanceID()] = true;
                if (maps[i].lightmapDir != null) used[maps[i].lightmapDir.GetInstanceID()] = true;
            }
            Take(RenderSettings.skybox, used, doneMaterials);
            return used;
        }

        private void Take(Material mat, Dictionary<int, bool> used, Dictionary<int, bool> doneMaterials)
        {
            if (ReferenceEquals(mat, null) || mat == null) return;
            int mid = mat.GetInstanceID();
            if (doneMaterials.ContainsKey(mid)) return;
            doneMaterials[mid] = true;
            for (int i = 0; i < m_slotIds.Length; i++)
            {
                if (!mat.HasProperty(m_slotIds[i])) continue;
                Texture t = mat.GetTexture(m_slotIds[i]);
                if (!ReferenceEquals(t, null) && t != null) used[t.GetInstanceID()] = true;
            }
        }
    }
}
