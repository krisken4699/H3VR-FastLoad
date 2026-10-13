using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FastLoad
{
    /// <summary>
    /// Test driver for runs nobody plays: when BepInEx/FastLoad/autotest.txt exists, its lines are carried out one
    /// after another once the first scene is up, and every step leaves "TST" lines in the run log (time taken,
    /// memory growth, worst frame). The probe's own lines in between show what each step made the game load.
    ///
    /// Steps (one per line, # starts a comment):
    ///   wait items | wait 12          until OtherLoader's "All items loaded" / that many seconds
    ///   census some-label             what is in memory now, to census_*.txt
    ///   dump                          every item id and sosig id, to items.txt and sosigs.txt
    ///   spawn text [count]            the item with that id, else the first [count] items whose id contains text
    ///   sosig text                    the sosig whose id is, or contains, text
    ///   model id                      CustomSosigModelFramework: pick that model (Sosig, Random or a pack id)
    ///   scene name                    load a game scene or a modded map, wait until it is there
    ///   clear                         remove what spawn and sosig made
    ///   check                         is what was spawned whole: renderers, missing materials, error shaders, missing meshes
    ///   profile 20 label              time every mod's per-frame code for 20 s, to lag_*.txt (patches stay in until the game closes)
    ///   icons drop | icons check      one pass of the icon governor / ask for a few icons and draw them, to see that they come back
    ///   census label counts           object counts only (the full census reads every dropped picture back in)
    ///   quit                          close the game
    /// </summary>
    public sealed class AutoTest : MonoBehaviour
    {
        public ManualLogSource Log;

        private readonly List<GameObject> m_made = new List<GameObject>();
        private readonly Stopwatch m_frame = new Stopwatch();
        private double m_worstFrameMs;
        private ItemsLoadedWatch m_items;
        private bool m_sceneCame;

        public static string ScriptPath()
        {
            return Path.Combine(Path.Combine(Paths.BepInExRootPath, "FastLoad"), "autotest.txt");
        }

        public void Begin()
        {
            m_items = new ItemsLoadedWatch();
            BepInEx.Logging.Logger.Listeners.Add(m_items);
            SceneManager.sceneLoaded += OnSceneLoaded;
            m_lines = File.ReadAllLines(ScriptPath());
            Say("script found: " + m_lines.Length + " lines. This run is driven by " + ScriptPath());
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive) m_sceneCame = true;
        }

        private void Say(string text)
        {
            Timeline.Write("TST", text);
            Log.LogInfo("AUTOTEST " + text);
        }

        // No coroutines: iterator classes built for net46 carry an attribute the game's runtime does not have.
        private string[] m_lines;
        private int m_next;
        private bool m_started;
        private bool m_over;
        private Step m_step;
        private string m_line = "";
        private string m_note = "";
        private long m_commit0;
        private readonly Stopwatch m_sinceStep = new Stopwatch();
        private readonly Stopwatch m_pause = new Stopwatch();
        private double m_pauseSeconds;
        private bool m_ending;

        private abstract class Step
        {
            public string Note = "";
            /// <summary>Called once a frame; true when the step is over.</summary>
            public abstract bool Tick(AutoTest test);
        }

        private void Update()
        {
            double ms = m_frame.Elapsed.TotalMilliseconds;
            m_frame.Reset();
            m_frame.Start();
            if (ms > m_worstFrameMs) m_worstFrameMs = ms;
            if (m_over || m_lines == null) return;

            if (m_pause.IsRunning)
            {
                if (m_pause.Elapsed.TotalSeconds < m_pauseSeconds) return;
                m_pause.Reset();
            }
            if (!m_started)
            {
                // The first scene, and a [Start] Scene jump if there is one.
                if (!m_sceneCame || StartScene.Busy) return;
                m_started = true;
                Pause(3.0);
                return;
            }
            if (m_ending)
            {
                m_ending = false;
                long sys, limit, commit, ws;
                Memory.Read(out commit, out ws, out sys, out limit);
                Say("end [" + m_line + "] " + m_note + " | took=" + (m_sinceStep.Elapsed.TotalMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + "s commit=" + (commit / 1048576) + "MB (" + ((commit - m_commit0) >= 0 ? "+" : "") + ((commit - m_commit0) / 1048576) + "MB) sysfree=" + (sys / 1048576)
                    + "MB worstFrame=" + (int)m_worstFrameMs + "ms scene=" + SceneManager.GetActiveScene().name);
                return;
            }
            if (m_step != null)
            {
                bool done;
                try
                {
                    done = m_step.Tick(this);
                    m_note = m_step.Note;
                }
                catch (Exception e)
                {
                    done = true;
                    m_note = "FAILED " + e.GetType().Name + ": " + e.Message;
                }
                if (done) EndStep();
                return;
            }
            NextLine();
        }

        private void Pause(double seconds)
        {
            m_pauseSeconds = seconds;
            m_pause.Reset();
            m_pause.Start();
        }

        private void EndStep()
        {
            m_step = null;
            m_ending = true;
            // What the step set going gets a moment to show up in the numbers.
            Pause(2.0);
        }

        private void NextLine()
        {
            while (m_next < m_lines.Length)
            {
                string line = m_lines[m_next++];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                int space = line.IndexOf(' ');
                string verb = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
                string rest = space < 0 ? "" : line.Substring(space + 1).Trim();

                m_line = line;
                m_note = "";
                m_commit0 = Memory.Commit();
                m_sinceStep.Reset();
                m_sinceStep.Start();
                m_worstFrameMs = 0;
                Say("begin [" + line + "]");
                try
                {
                    if (verb == "wait") m_step = rest == "items" ? (Step)new WaitItems() : new WaitSeconds(double.Parse(rest, CultureInfo.InvariantCulture));
                    else if (verb == "census") m_note = Census.Write(rest);
                    else if (verb == "dump") m_note = Dump();
                    else if (verb == "spawn") m_step = new SpawnStep(rest);
                    else if (verb == "sosig") m_note = SosigDoor.Spawn(rest, Front(3f, 0f), m_made);
                    else if (verb == "model") m_note = PickModel(rest);
                    else if (verb == "scene") m_step = new SceneStep(rest);
                    else if (verb == "clear") m_note = Clear();
                    else if (verb == "check") m_note = Check();
                    else if (verb == "inputoff") m_note = InputOff();
                    else if (verb == "steamvroff") m_note = SteamVrOff();
                    else if (verb == "hands") m_note = HandsNow();
                    else if (verb == "look") m_note = Look();
                    else if (verb == "iconlook") m_note = IconLook(rest);
                    else if (verb == "iconaudit") m_note = IconAudit();
                    else if (verb == "iconhold") m_step = new IconHoldStep(this);
                    else if (verb == "droptex") m_note = DropTextures();
                    else if (verb == "touch") m_note = Touch(rest);
                    else if (verb == "guard") { HandGuard.Paused = rest == "off"; m_note = "hand guard " + (HandGuard.Paused ? "watching only" : "repairing"); }
                    else if (verb == "directscene") m_step = new DirectSceneStep(rest);
                    else if (verb == "roots") m_note = Roots();
                    else if (verb == "profile") m_step = new ProfileStep(rest);
                    else if (verb == "bundleprobe" || verb == "bench" || verb == "pick") m_step = new LabStep(verb, rest, m_made);
                    else if (verb == "standins") m_note = LoadLab.StandIns();
                    else if (verb == "icons") m_note = rest == "drop" ? Icons.UnloadAll() : rest == "check" ? Icons.Check() : "icons drop | icons check";
                    else if (verb == "quit")
                    {
                        m_over = true;
                        Say("script done");
                        Application.Quit();
                        return;
                    }
                    else m_note = "UNKNOWN STEP";
                }
                catch (Exception e)
                {
                    Exception inner = e.InnerException ?? e;
                    m_step = null;
                    m_note = "FAILED " + inner.GetType().Name + ": " + inner.Message;
                }
                if (m_step == null) EndStep();
                return;
            }
            m_over = true;
            Say("script done");
        }

        private sealed class WaitSeconds : Step
        {
            private readonly double m_seconds;
            private readonly Stopwatch m_sw = Stopwatch.StartNew();
            public WaitSeconds(double seconds) { m_seconds = seconds; }
            public override bool Tick(AutoTest test) { return m_sw.Elapsed.TotalSeconds >= m_seconds; }
        }

        /// <summary>"profile 20 label": times mod code for that many seconds.</summary>
        private sealed class ProfileStep : Step
        {
            private readonly Stopwatch m_sw = new Stopwatch();
            private readonly double m_seconds = 20;
            private readonly string m_label = "profile";
            private readonly string m_setup;

            public ProfileStep(string rest)
            {
                string[] parts = rest.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) m_seconds = double.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts.Length > 1) m_label = parts[1];
                m_setup = LagProfiler.Install();
            }

            public override bool Tick(AutoTest test)
            {
                if (!m_sw.IsRunning)
                {
                    // The frame the patches went in is not a normal one.
                    LagProfiler.Begin();
                    m_sw.Start();
                    return false;
                }
                if (m_sw.Elapsed.TotalSeconds < m_seconds) return false;
                Note = LagProfiler.End(m_label) + " | " + m_setup;
                return true;
            }
        }

        private sealed class LabStep : Step
        {
            public LabStep(string verb, string rest, List<GameObject> made)
            {
                LoadLab.Begin(verb, rest, made);
            }

            public override bool Tick(AutoTest test)
            {
                bool over = LoadLab.Tick();
                for (int i = 0; i < LoadLab.Lines.Count; i++) test.Say(LoadLab.Lines[i]);
                LoadLab.Lines.Clear();
                if (over) Note = LoadLab.Note;
                return over;
            }
        }

        private sealed class WaitItems : Step
        {
            private readonly Stopwatch m_sw = Stopwatch.StartNew();
            public override bool Tick(AutoTest test)
            {
                if (test.m_items.Seen) { Note = "items loaded"; return true; }
                if (m_sw.Elapsed.TotalSeconds < 600) return false;
                Note = "NO 'All items loaded' AFTER 600 s";
                return true;
            }
        }

        private sealed class SpawnStep : Step
        {
            private readonly List<FVRObject> m_picks = new List<FVRObject>();
            private int m_index;
            private AnvilCallback<GameObject> m_load;
            private readonly Stopwatch m_sw = new Stopwatch();
            private long m_before;
            private string m_made = "";

            public SpawnStep(string rest)
            {
                int count = 1;
                string text = rest;
                int space = rest.LastIndexOf(' ');
                int n;
                if (space > 0 && int.TryParse(rest.Substring(space + 1), out n))
                {
                    count = n;
                    text = rest.Substring(0, space).Trim();
                }
                FVRObject exact;
                if (IM.OD.TryGetValue(text, out exact) && exact != null) m_picks.Add(exact);
                else
                {
                    foreach (KeyValuePair<string, FVRObject> pair in IM.OD)
                    {
                        if (pair.Value == null || pair.Key.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        m_picks.Add(pair.Value);
                        if (m_picks.Count >= count) break;
                    }
                }
                if (m_picks.Count == 0) Note = "NO ITEM id contains '" + text + "'";
            }

            public override bool Tick(AutoTest test)
            {
                if (m_index >= m_picks.Count) return true;
                FVRObject item = m_picks[m_index];
                if (m_load == null)
                {
                    m_sw.Reset();
                    m_sw.Start();
                    m_before = Memory.Commit();
                    m_load = item.GetGameObjectAsync();
                    if (m_load == null)
                    {
                        Done(test, item, null);
                        return false;
                    }
                }
                if (!m_load.IsCompleted && m_sw.Elapsed.TotalSeconds < 120) return false;
                Done(test, item, m_load.IsCompleted ? m_load.Result : null);
                return false;
            }

            private void Done(AutoTest test, FVRObject item, GameObject prefab)
            {
                string one = item.ItemID + " (bundle " + ItemBundle(item) + ")";
                if (prefab == null) one += " PREFAB DID NOT LOAD";
                else
                {
                    GameObject go = Instantiate(prefab, Front(0.6f + 0.15f * m_index, 0.1f), Quaternion.identity);
                    test.m_made.Add(go);
                    one += " ok " + (int)m_sw.Elapsed.TotalMilliseconds + "ms +" + ((Memory.Commit() - m_before) / 1048576) + "MB";
                }
                test.Say("spawned " + one);
                m_made += (m_made.Length == 0 ? "" : "; ") + one;
                m_load = null;
                m_index++;
                Note = m_index + " item(s): " + m_made;
            }
        }

        private sealed class SceneStep : Step
        {
            private readonly Stopwatch m_sw = Stopwatch.StartNew();
            private readonly Stopwatch m_tail = new Stopwatch();
            private readonly bool m_asked;
            private readonly string m_what = "";

            public SceneStep(string name)
            {
                Pick pick = StartPick.Resolve(name, GameSceneNames(), AtlasBridge.Maps());
                if (pick.Kind == PickKind.Game)
                {
                    SteamVR_LoadLevel.Begin(pick.Value, false, 0.5f, 0f, 0f, 0f, 1f);
                    m_asked = true;
                }
                else if (pick.Kind == PickKind.Modded)
                {
                    string error;
                    m_asked = AtlasBridge.Load(pick.Value, out error);
                    if (!m_asked) Note = "ATLAS COULD NOT LOAD '" + pick.Value + "': " + error;
                }
                else Note = "NO SCENE OR MAP '" + name + "' (" + pick.Kind + " " + pick.Note + ")";
                m_what = pick.Kind + " scene '" + pick.Value + "'";
            }

            public override bool Tick(AutoTest test)
            {
                if (!m_asked) return true;
                if (!m_tail.IsRunning)
                {
                    if (m_sw.ElapsedMilliseconds < 500) { test.m_sceneCame = false; return false; }
                    if (!test.m_sceneCame)
                    {
                        if (m_sw.Elapsed.TotalSeconds < 180) return false;
                        Note = "SCENE DID NOT ARRIVE IN 180 s";
                        return true;
                    }
                    Note = m_what + " arrived after " + (m_sw.Elapsed.TotalMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
                    test.m_made.Clear();
                    // The loader's own tail (settle, fade in) and the scene's first frames.
                    m_tail.Start();
                    return false;
                }
                return m_tail.Elapsed.TotalSeconds >= 5.0;
            }
        }

        private static Vector3 Front(float distance, float up)
        {
            Transform head = GM.CurrentPlayerBody != null ? GM.CurrentPlayerBody.Head : null;
            if (head == null) return new Vector3(0f, 1.2f + up, 0f);
            Vector3 flat = head.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;
            return head.position + flat.normalized * distance + Vector3.up * up;
        }

        private static readonly string[] TexSlots = new string[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_EmissionMap",
            "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask", "_ParallaxMap", "_SpecTex", "_MaterialMap", "_PackedMap", "_BumpMap2", "_MainTex2", "_MaterialMap2",
            "_EmissionMap2", "_DecalTex", "_Illum", "_Detail", "_IncandescenceMap", "_RimTex", "_Rma", "_MaskTex", "_NormalMap", "_AlloyPM", "_BaseColor" };

        private GameObject LastMade()
        {
            for (int i = m_made.Count - 1; i >= 0; i--) if (m_made[i] != null) return m_made[i];
            return null;
        }

        /// <summary>
        /// A small picture of the last spawned thing, taken by a camera of its own against a magenta backdrop:
        /// how much of the picture it covers and how bright it is. A gun whose textures are gone is black.
        /// </summary>
        private string Look()
        {
            GameObject go = LastMade();
            if (go == null) return "NOTHING SPAWNED";
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(false);
            if (rs.Length == 0) return "NO RENDERERS";
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) if (!(rs[i] is ParticleSystemRenderer)) b.Encapsulate(rs[i].bounds);
            float size = Mathf.Max(0.1f, b.extents.magnitude);
            GameObject camGo = new GameObject("FastLoad test camera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1f, 0f, 1f, 1f);
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = size * 6f + 2f;
            cam.fieldOfView = 40f;
            camGo.transform.position = b.center + new Vector3(0.6f, 0.4f, 0.7f).normalized * (size * 2.4f);
            camGo.transform.LookAt(b.center);
            RenderTexture rt = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            // Only the thing itself in the picture: it goes on a layer of its own for the one render.
            Transform[] parts = go.GetComponentsInChildren<Transform>(true);
            int[] layers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                layers[i] = parts[i].gameObject.layer;
                parts[i].gameObject.layer = 31;
            }
            cam.cullingMask = 1 << 31;
            try
            {
                cam.Render();
            }
            finally
            {
                for (int i = 0; i < parts.Length; i++) if (parts[i] != null) parts[i].gameObject.layer = layers[i];
            }
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D read = new Texture2D(96, 96, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 96, 96), 0, 0);
            RenderTexture.active = before;
            Color32[] px = read.GetPixels32();
            int covered = 0, dark = 0;
            long lum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                // The backdrop: strong red and blue, no green.
                if (c.r > 200 && c.b > 200 && c.g < 60) continue;
                covered++;
                int l = (c.r * 2 + c.g * 5 + c.b) / 8;
                lum += l;
                if (l < 12) dark++;
            }
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Destroy(read);
            Destroy(camGo);
            if (covered == 0) return "OBJECT NOT IN THE PICTURE";
            return go.name + ": covers " + (covered * 100 / px.Length) + "% of the picture, brightness " + (lum / covered) + "/255, near-black " + (dark * 100 / covered) + "% of it";
        }

        /// <summary>
        /// "iconlook 12 [nohook]": puts 12 item-spawner icons on UI images of a test canvas, the way the spawner does
        /// (image.sprite = icon), takes a picture of the canvas alone and says how much of it is not blank.
        /// With "nohook" the pictures are not read back in when the sprite is handed over: what a spawner page
        /// looked like before the fix.
        /// </summary>
        private string IconLook(string rest)
        {
            IconGovernor g = IconGovernor.Instance;
            if (g == null) return "ITEM ICONS ON DEMAND IS OFF";
            string[] parts = rest.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int count = parts.Length > 0 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 12;
            bool noHook = parts.Length > 1 && parts[1] == "nohook";
            List<Sprite> sprites = g.KnownSprites();
            if (sprites.Count == 0) return "NO ICONS KNOWN";
            int before = IconGovernor.BroughtBack;

            GameObject root = new GameObject("FastLoad test canvas");
            root.layer = 31;
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform rtf = root.GetComponent<RectTransform>();
            rtf.sizeDelta = new Vector2(400f, 300f);
            root.transform.position = new Vector3(0f, -200f, 0f);
            root.transform.localScale = Vector3.one * 0.01f;
            int step = Math.Max(1, sprites.Count / count);
            int made = 0;
            IconGovernor.HookPaused = noHook;
            try
            {
                for (int i = m_iconCursor; i < sprites.Count && made < count; i += step)
                {
                    if (sprites[i] == null) continue;
                    GameObject cell = new GameObject("icon");
                    cell.layer = 31;
                    cell.transform.SetParent(root.transform, false);
                    UnityEngine.UI.Image img = cell.AddComponent<UnityEngine.UI.Image>();
                    RectTransform r = cell.GetComponent<RectTransform>();
                    r.sizeDelta = new Vector2(90f, 90f);
                    r.anchoredPosition = new Vector2(-150f + (made % 4) * 100f, 100f - (made / 4) * 100f);
                    img.sprite = sprites[i];
                    made++;
                }
            }
            finally
            {
                IconGovernor.HookPaused = false;
            }
            // Other icons next time, so one call does not warm up the next.
            m_iconCursor += 7;
            string result = made + " icons on a test canvas" + (noHook ? " WITHOUT reading them back" : "") + ": " + PictureOf(root, made)
                + "; read back by the hook during this: " + (IconGovernor.BroughtBack - before);
            Destroy(root);
            return result;
        }

        /// <summary>Takes a picture of a test canvas alone and counts which of its icon squares show a picture.</summary>
        private static string PictureOf(GameObject root, int made)
        {
            Canvas.ForceUpdateCanvases();

            GameObject camGo = new GameObject("FastLoad test camera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1f, 0f, 1f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 1.6f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 10f;
            cam.cullingMask = 1 << 31;
            camGo.transform.position = root.transform.position + new Vector3(0f, 0f, -3f);
            camGo.transform.rotation = Quaternion.identity;
            RenderTexture rt = RenderTexture.GetTemporary(128, 96, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D read = new Texture2D(128, 96, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 128, 96), 0, 0);
            RenderTexture.active = was;
            Color32[] px = read.GetPixels32();
            // Each icon's square by itself: a picture has light and dark in it, a square with no picture is one flat colour.
            int flat = 0, pictures = 0, missing = 0;
            for (int n = 0; n < made; n++)
            {
                // Canvas units to pixels: 300 units = 3 world units = 90 of the 96 pixel rows (orthographic size 1.6).
                float cx = (-150f + (n % 4) * 100f) * 0.01f, cy = (100f - (n / 4) * 100f) * 0.01f;
                int px0 = 64 + (int)(cx * 30f), py0 = 48 + (int)(cy * 30f);
                double sum = 0, sumSq = 0;
                int k = 0;
                for (int y = py0 - 9; y <= py0 + 9; y++)
                {
                    for (int x = px0 - 9; x <= px0 + 9; x++)
                    {
                        if (x < 0 || y < 0 || x >= 128 || y >= 96) continue;
                        Color32 c = px[y * 128 + x];
                        if (c.r > 200 && c.b > 200 && c.g < 60) continue;
                        double l = (c.r * 2 + c.g * 5 + c.b) / 8.0;
                        sum += l;
                        sumSq += l * l;
                        k++;
                    }
                }
                if (k < 50) { missing++; continue; }
                double mean = sum / k;
                double dev = Math.Sqrt(Math.Max(0, sumSq / k - mean * mean));
                if (dev < 2.0)
                {
                    flat++;
                    // Which one, and is its texture there at all: a flat square can be a missing picture or simply a plain icon.
                    try
                    {
                        UnityEngine.UI.Image[] imgs = root.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                        if (n < imgs.Length && imgs[n].sprite != null)
                        {
                            Sprite sp = imgs[n].sprite;
                            Texture2D tx = sp.texture;
                            s_flatNotes += " [flat: sprite '" + sp.name + "' texture " + (ReferenceEquals(tx, null) ? "null" : tx == null ? "DEAD" : "'" + tx.name + "' " + tx.width + "x" + tx.height
                                + " id" + (tx.GetInstanceID() > 0 ? "+" : "-")) + " rect " + (int)sp.rect.width + "x" + (int)sp.rect.height + " colour " + (int)mean + "]";
                        }
                    }
                    catch (Exception e)
                    {
                        s_flatNotes += " [flat: " + e.GetType().Name + "]";
                    }
                }
                else pictures++;
            }
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Destroy(read);
            Destroy(camGo);
            string notes = s_flatNotes;
            s_flatNotes = "";
            return pictures + " show a picture, " + flat + " are a flat square, " + missing + " not drawn" + notes + " (kept for closed files so far: " + IconGovernor.KeptForClosedFiles + ")";
        }

        /// <summary>
        /// What a spawner page goes through: 12 icons on images, the panel is switched off for a while (with the
        /// idle time at nothing, so the governor drops whatever it thinks is free), then switched on again.
        /// </summary>
        private sealed class IconHoldStep : Step
        {
            private readonly GameObject m_root;
            private readonly Stopwatch m_sw = Stopwatch.StartNew();
            private readonly double m_idleWas;
            private readonly string m_first;
            private readonly long m_droppedBefore;
            private int m_phase;

            public IconHoldStep(AutoTest test)
            {
                IconGovernor g = IconGovernor.Instance;
                if (g == null) { Note = "ITEM ICONS ON DEMAND IS OFF"; m_phase = 9; return; }
                List<Sprite> sprites = g.KnownSprites();
                m_root = new GameObject("FastLoad test canvas (hold)");
                m_root.layer = 31;
                Canvas canvas = m_root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                m_root.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 300f);
                m_root.transform.position = new Vector3(0f, -200f, 0f);
                m_root.transform.localScale = Vector3.one * 0.01f;
                int step = Math.Max(1, sprites.Count / 12);
                int made = 0;
                for (int i = 3; i < sprites.Count && made < 12; i += step)
                {
                    if (sprites[i] == null) continue;
                    GameObject cell = new GameObject("icon");
                    cell.layer = 31;
                    cell.transform.SetParent(m_root.transform, false);
                    UnityEngine.UI.Image img = cell.AddComponent<UnityEngine.UI.Image>();
                    RectTransform r = cell.GetComponent<RectTransform>();
                    r.sizeDelta = new Vector2(90f, 90f);
                    r.anchoredPosition = new Vector2(-150f + (made % 4) * 100f, 100f - (made / 4) * 100f);
                    img.sprite = sprites[i];
                    made++;
                }
                m_first = PictureOf(m_root, 12);
                m_idleWas = Unloading.IdleSeconds;
                Unloading.IdleSeconds = 0.0;
                m_droppedBefore = g.Dropped;
                m_root.SetActive(false);
            }

            public override bool Tick(AutoTest test)
            {
                if (m_phase == 9) return true;
                if (m_sw.Elapsed.TotalSeconds < 6.0) return false;
                IconGovernor g = IconGovernor.Instance;
                long dropped = g != null ? g.Dropped - m_droppedBefore : -1;
                m_root.SetActive(true);
                string second = PictureOf(m_root, 12);
                Unloading.IdleSeconds = m_idleWas;
                Destroy(m_root);
                Note = "shown: " + m_first + " | hidden 6 s with no idle time (governor dropped " + dropped + " pictures meanwhile) | shown again: " + second;
                return true;
            }
        }

        /// <summary>Every known icon asked for its picture: how many have none left. Reads them all back in.</summary>
        private static string IconAudit()
        {
            IconGovernor g = IconGovernor.Instance;
            if (g == null) return "ITEM ICONS ON DEMAND IS OFF";
            List<Sprite> sprites = g.KnownSprites();
            int ok = 0, lost = 0, born = 0;
            string names = "";
            for (int i = 0; i < sprites.Count; i++)
            {
                if (sprites[i] == null) continue;
                Texture2D t = sprites[i].texture;
                if (!ReferenceEquals(t, null) && t != null) { ok++; g.Touched(t); continue; }
                if (IconGovernor.BornEmpty.ContainsKey(sprites[i].GetInstanceID())) { born++; continue; }
                lost++;
                if (lost <= 20)
                {
                    names += " '" + sprites[i].name + "'";
                    // Whose icon it is.
                    UnityEngine.Object[] ids = Resources.FindObjectsOfTypeAll(typeof(ItemSpawnerID));
                    for (int k = 0; k < ids.Length; k++)
                    {
                        ItemSpawnerID sid = ids[k] as ItemSpawnerID;
                        if (sid == null || !ReferenceEquals(sid.Sprite, sprites[i])) continue;
                        names += "(item " + sid.ItemID + ", bundle " + (sid.MainObject != null ? ItemBundle(sid.MainObject) : "?") + ", entry '" + sid.name + "')";
                    }
                }
            }
            return "of " + (ok + lost + born) + " icons " + born + " came without a picture, " + lost + " have lost their picture for good" + (lost > 0 ? ":" + names : "") + "; protected for closed files: " + IconGovernor.KeptForClosedFiles;
        }

        private int m_iconCursor;
        private static string s_flatNotes = "";

        private List<KeyValuePair<Material, int>> SlotsOfLast(out int materials)
        {
            List<KeyValuePair<Material, int>> list = new List<KeyValuePair<Material, int>>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            materials = 0;
            GameObject go = LastMade();
            if (go == null) return list;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rs.Length; r++)
            {
                Material[] mats = rs[r].sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null || seen.ContainsKey(mats[m].GetInstanceID())) continue;
                    seen[mats[m].GetInstanceID()] = true;
                    materials++;
                    for (int k = 0; k < TexSlots.Length; k++)
                    {
                        int id = Shader.PropertyToID(TexSlots[k]);
                        if (mats[m].HasProperty(id)) list.Add(new KeyValuePair<Material, int>(mats[m], id));
                    }
                }
            }
            return list;
        }

        /// <summary>Drops every texture found on the last spawned thing's materials, as the texture governor would.</summary>
        private string DropTextures()
        {
            int materials;
            List<KeyValuePair<Material, int>> slots = SlotsOfLast(out materials);
            Dictionary<int, bool> done = new Dictionary<int, bool>();
            int dropped = 0, runtime = 0, empty = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Texture t = slots[i].Key.GetTexture(slots[i].Value);
                if (ReferenceEquals(t, null) || t == null) { empty++; continue; }
                if (done.ContainsKey(t.GetInstanceID())) continue;
                done[t.GetInstanceID()] = true;
                if (t.GetInstanceID() < 0) { runtime++; continue; }
                Resources.UnloadAsset(t);
                dropped++;
            }
            return "dropped " + dropped + " textures of " + materials + " materials (" + slots.Count + " slots, " + empty + " empty, " + runtime + " made at run time)";
        }

        /// <summary>Ways a dropped texture might be brought back: "get" asks the material for it, "rebind" also sets it again, "size" also reads its size.</summary>
        private string Touch(string how)
        {
            int materials;
            List<KeyValuePair<Material, int>> slots = SlotsOfLast(out materials);
            int got = 0, nulls = 0, fake = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Texture t = slots[i].Key.GetTexture(slots[i].Value);
                if (ReferenceEquals(t, null)) { nulls++; continue; }
                if (t == null) { fake++; continue; }
                got++;
                if (how == "size") { int w = t.width; if (w < 0) got--; }
                if (how == "rebind") slots[i].Key.SetTexture(slots[i].Value, t);
            }
            return "touch " + how + ": " + got + " textures answered, " + fake + " slots hold a dead reference, " + nulls + " slots empty";
        }

        private string HandsNow()
        {
            FVRMovementManager mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null) return "no hands";
            string text = "";
            for (int i = 0; i < mm.Hands.Length; i++)
            {
                string one = HandGuard.Describe(mm.Hands[i]);
                Say(one);
                text += (i == 0 ? "" : " || ") + one;
            }
            return mm.Hands.Length + " hands described above";
        }

        /// <summary>The scene loaded with Unity's own call, not the game's loader: the way that left hands parked.</summary>
        private sealed class DirectSceneStep : Step
        {
            private readonly Stopwatch m_sw = Stopwatch.StartNew();
            private readonly Stopwatch m_tail = new Stopwatch();
            private readonly bool m_asked;

            public DirectSceneStep(string name)
            {
                m_asked = SceneManager.LoadSceneAsync(name, LoadSceneMode.Single) != null;
                if (!m_asked) Note = "UNITY WOULD NOT LOAD '" + name + "'";
            }

            public override bool Tick(AutoTest test)
            {
                if (!m_asked) return true;
                if (!m_tail.IsRunning)
                {
                    if (m_sw.ElapsedMilliseconds < 300) { test.m_sceneCame = false; return false; }
                    if (!test.m_sceneCame) return m_sw.Elapsed.TotalSeconds > 120;
                    Note = "arrived after " + (m_sw.Elapsed.TotalMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
                    test.m_made.Clear();
                    m_tail.Start();
                    return false;
                }
                return m_tail.Elapsed.TotalSeconds >= 4.0;
            }
        }

        /// <summary>
        /// Switches the SteamVR object's component off, in this scene and (because it is asked for again after a load)
        /// the next: its camera callback goes away and with it the update that moves the hands.
        /// </summary>
        private static string SteamVrOff()
        {
            Valve.VR.SteamVR_Behaviour[] all = Resources.FindObjectsOfTypeAll(typeof(Valve.VR.SteamVR_Behaviour)) as Valve.VR.SteamVR_Behaviour[];
            int n = 0;
            for (int i = 0; all != null && i < all.Length; i++)
            {
                if (all[i] == null || !all[i].enabled) continue;
                all[i].enabled = false;
                n++;
            }
            return "switched off " + n + " SteamVR component(s)";
        }

        /// <summary>Switches every SteamVR input set off, the state a bad scene change leaves behind. The hand guard should undo it.</summary>
        private static string InputOff()
        {
            Valve.VR.SteamVR_ActionSet[] sets = Valve.VR.SteamVR_Input.actionSets;
            string names = "";
            for (int i = 0; sets != null && i < sets.Length; i++)
            {
                if (sets[i] == null || !sets[i].IsActive(Valve.VR.SteamVR_Input_Sources.Any)) continue;
                sets[i].Deactivate(Valve.VR.SteamVR_Input_Sources.Any);
                names += (names.Length == 0 ? "" : ", ") + sets[i].GetShortName();
            }
            return names.Length == 0 ? "no input set was on" : "switched off: " + names;
        }

        /// <summary>The same numbers with a feature on and off = the feature left nothing out.</summary>
        private string Check()
        {
            int objects = 0, renderers = 0, noMaterial = 0, errorShader = 0, noMesh = 0, components = 0, transforms = 0;
            for (int i = 0; i < m_made.Count; i++)
            {
                GameObject go = m_made[i];
                if (go == null) continue;
                objects++;
                transforms += go.GetComponentsInChildren<Transform>(true).Length;
                components += go.GetComponentsInChildren<Component>(true).Length;
                Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
                renderers += rs.Length;
                for (int r = 0; r < rs.Length; r++)
                {
                    if (rs[r] is ParticleSystemRenderer) continue;
                    Material[] mats = rs[r].sharedMaterials;
                    if (mats.Length == 0) noMaterial++;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] == null) noMaterial++;
                        else if (mats[m].shader == null || mats[m].shader.name == "Hidden/InternalErrorShader") errorShader++;
                    }
                }
                MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
                for (int f = 0; f < filters.Length; f++) if (filters[f].sharedMesh == null) noMesh++;
            }
            return "objects=" + objects + " transforms=" + transforms + " components=" + components + " renderers=" + renderers + " missingMaterial=" + noMaterial
                + " errorShader=" + errorShader + " missingMesh=" + noMesh;
        }

        /// <summary>Top-level objects of the scene by name: what a mod put where.</summary>
        private static string Roots()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            string names = "";
            for (int i = 0; i < roots.Length; i++) names += (i == 0 ? "" : " | ") + roots[i].name;
            return scene.name + " has " + roots.Length + " top-level objects: " + names;
        }

        private string Clear()
        {
            int n = 0;
            for (int i = 0; i < m_made.Count; i++)
            {
                if (m_made[i] == null) continue;
                Destroy(m_made[i]);
                n++;
            }
            m_made.Clear();
            return "removed " + n;
        }

        private static string PickModel(string id)
        {
            Type plugin = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "CustomSosigModelFramework") continue;
                plugin = a.GetType("CustomSosigReplacer.Plugin");
            }
            if (ReferenceEquals(plugin, null)) return "CustomSosigModelFramework IS NOT LOADED";
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            plugin.GetMethod("SelectModel", any).Invoke(null, new object[] { id });
            object now = plugin.GetProperty("SelectedModel", any).GetValue(null, null);
            return "selected model is now '" + now + "'";
        }

        private static List<string> GameSceneNames()
        {
            List<string> names = new List<string>();
            int n = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < n; i++)
            {
                string name = StartPick.SceneNameFromPath(SceneUtility.GetScenePathByBuildIndex(i));
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        private static string ItemBundle(FVRObject item)
        {
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo f = typeof(AnvilAsset).GetField("m_anvilPrefab", any);
                object id = ReferenceEquals(f, null) ? null : f.GetValue(item);
                if (id == null) return "?";
                FieldInfo b = id.GetType().GetField("Bundle", any);
                object bundle = ReferenceEquals(b, null) ? null : b.GetValue(id);
                string s = bundle as string;
                return string.IsNullOrEmpty(s) ? "?" : Path.GetFileName(s);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Dump()
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "FastLoad");
            StringBuilder sb = new StringBuilder();
            sb.Append("ItemID\tCategory\tmod content\tbundle\tDisplayName\n");
            int items = 0;
            foreach (KeyValuePair<string, FVRObject> pair in IM.OD)
            {
                FVRObject o = pair.Value;
                if (o == null) continue;
                sb.Append(pair.Key).Append('\t').Append(o.Category).Append('\t').Append(o.IsModContent ? "mod" : "game").Append('\t')
                    .Append(ItemBundle(o)).Append('\t').Append(o.DisplayName).Append('\n');
                items++;
            }
            File.WriteAllText(Path.Combine(dir, "items.txt"), sb.ToString());

            sb = new StringBuilder();
            int sosigs = 0;
            foreach (KeyValuePair<SosigEnemyID, SosigEnemyTemplate> pair in ManagerSingleton<IM>.Instance.odicSosigObjsByID)
            {
                sb.Append(pair.Key).Append('\t').Append((int)pair.Key).Append('\t').Append(pair.Value != null ? pair.Value.DisplayName : "").Append('\n');
                sosigs++;
            }
            File.WriteAllText(Path.Combine(dir, "sosigs.txt"), sb.ToString());
            return items + " items -> items.txt, " + sosigs + " sosigs -> sosigs.txt";
        }
    }

    /// <summary>The only place that touches Sodalite, so a profile without it only loses the sosig step.</summary>
    internal static class SosigDoor
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Spawn(string text, Vector3 position, List<GameObject> made)
        {
            SosigEnemyTemplate template = null;
            string picked = "";
            foreach (KeyValuePair<SosigEnemyID, SosigEnemyTemplate> pair in ManagerSingleton<IM>.Instance.odicSosigObjsByID)
            {
                string id = pair.Key.ToString();
                bool exact = string.Equals(id, text, StringComparison.OrdinalIgnoreCase);
                if (!exact && (template != null || id.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                template = pair.Value;
                picked = id;
                if (exact) break;
            }
            if (template == null) return "NO SOSIG id contains '" + text + "'";
            Sodalite.Api.SosigAPI.SpawnOptions options = new Sodalite.Api.SosigAPI.SpawnOptions();
            options.SpawnActivated = true;
            options.IFF = 0;
            options.SpawnWithFullAmmo = true;
            options.SosigTargetPosition = position;
            Sosig sosig = Sodalite.Api.SosigAPI.Spawn(template, options, position, Quaternion.identity);
            if (sosig == null) return "SOSIG " + picked + " DID NOT SPAWN";
            made.Add(sosig.gameObject);
            return "sosig " + picked + " spawned";
        }
    }

    /// <summary>What is in memory, by kind of thing, with the biggest of each kind by name.</summary>
    internal static class Census
    {
        private const int Top = 25;

        public static string Write(string label)
        {
            // "census name counts": object counts only. Asking a dropped picture for its size reads it back in.
            bool countsOnly = label.EndsWith(" counts", StringComparison.Ordinal);
            if (countsOnly) label = label.Substring(0, label.Length - 7).Trim();
            if (label.Length == 0) label = "census";
            Stopwatch sw = Stopwatch.StartNew();
            StringBuilder sb = new StringBuilder();
            long commit, ws, sys, limit;
            Memory.Read(out commit, out ws, out sys, out limit);
            sb.Append("FastLoad memory census '").Append(label).Append("' at ").Append(Clock.NowMs / 1000).Append(" s, scene ").Append(SceneManager.GetActiveScene().name).Append('\n');
            sb.Append("process commit ").Append(commit / 1048576).Append(" MB, working set ").Append(ws / 1048576).Append(" MB, system commit free ").Append(sys / 1048576).Append(" MB\n");
            sb.Append("Unity: allocated ").Append(Mb(Profile("GetTotalAllocatedMemory"))).Append(" MB, reserved ").Append(Mb(Profile("GetTotalReservedMemory")))
                .Append(" MB, reserved unused ").Append(Mb(Profile("GetTotalUnusedReservedMemory"))).Append(" MB, mono heap ").Append(Mb(Profile("GetMonoHeapSize")))
                .Append(" MB, mono used ").Append(Mb(Profile("GetMonoUsedSize"))).Append(" MB\n");
            sb.Append("size = what Unity reports for the object; est = worked out from its dimensions (textures: 4 bytes a pixel + mips; audio: decoded samples).\n\n");

            string summary = "";
            Type[] kinds = new Type[] { typeof(Texture2D), typeof(Cubemap), typeof(RenderTexture), typeof(Mesh), typeof(AudioClip), typeof(AnimationClip),
                typeof(Material), typeof(Shader), typeof(Sprite), typeof(Font), typeof(GameObject), typeof(ScriptableObject), typeof(MonoBehaviour), typeof(TextAsset) };
            for (int k = 0; k < kinds.Length; k++)
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(kinds[k]);
                long total = 0;
                long est = 0;
                bool sized = !countsOnly && !ReferenceEquals(kinds[k], typeof(GameObject)) && !ReferenceEquals(kinds[k], typeof(MonoBehaviour)) && !ReferenceEquals(kinds[k], typeof(ScriptableObject));
                List<KeyValuePair<long, string>> big = new List<KeyValuePair<long, string>>();
                for (int i = 0; i < all.Length; i++)
                {
                    UnityEngine.Object o = all[i];
                    if (o == null || !sized) continue;
                    long size = 0;
                    try
                    {
                        size = Convert.ToInt64(UnityEngine.Profiling.Profiler.GetRuntimeMemorySize(o));
                    }
                    catch (Exception)
                    {
                    }
                    long e = Estimate(o);
                    total += size;
                    est += e;
                    long rank = Math.Max(size, e);
                    if (big.Count < Top || rank > big[big.Count - 1].Key)
                    {
                        big.Add(new KeyValuePair<long, string>(rank, o.name + Detail(o) + " size=" + Mb(size) + "MB est=" + Mb(e) + "MB"));
                        big.Sort(delegate(KeyValuePair<long, string> a, KeyValuePair<long, string> b) { return b.Key.CompareTo(a.Key); });
                        if (big.Count > Top) big.RemoveAt(big.Count - 1);
                    }
                }
                sb.Append("== ").Append(kinds[k].Name).Append(": ").Append(all.Length).Append(" objects");
                if (sized) sb.Append(", size ").Append(Mb(total)).Append(" MB, est ").Append(Mb(est)).Append(" MB");
                sb.Append('\n');
                for (int i = 0; i < big.Count; i++) sb.Append("   ").Append(big[i].Value).Append('\n');
                summary += kinds[k].Name + "=" + all.Length + (sized ? "/" + Mb(total) + "MB/est" + Mb(est) + "MB" : "") + " ";
            }

            if (!countsOnly) try
            {
                List<Texture2D> icons = Icons.Textures();
                long iconEst = 0;
                for (int i = 0; i < icons.Count; i++) iconEst += Estimate(icons[i]);
                sb.Append("== Item spawner icons: ").Append(icons.Count).Append(" textures, est ").Append(Mb(iconEst)).Append(" MB\n");
                summary += "IconTextures=" + icons.Count + "/est" + Mb(iconEst) + "MB ";

                Dictionary<string, long[]> byFormat = new Dictionary<string, long[]>();
                UnityEngine.Object[] textures = Resources.FindObjectsOfTypeAll(typeof(Texture2D));
                for (int i = 0; i < textures.Length; i++)
                {
                    Texture2D t = textures[i] as Texture2D;
                    if (t == null) continue;
                    string key = t.format + (t.mipmapCount > 1 ? " mips" : " no-mips") + (Math.Max(t.width, t.height) >= 1024 ? " >=1024" : Math.Max(t.width, t.height) >= 512 ? " 512+" : " small");
                    long[] row;
                    if (!byFormat.TryGetValue(key, out row)) byFormat[key] = row = new long[2];
                    row[0]++;
                    row[1] += Estimate(t);
                }
                List<KeyValuePair<string, long[]>> rows = new List<KeyValuePair<string, long[]>>(byFormat);
                rows.Sort(delegate(KeyValuePair<string, long[]> a, KeyValuePair<string, long[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
                sb.Append("== Texture2D by format and size\n");
                for (int i = 0; i < rows.Count && i < 20; i++) sb.Append("   ").Append(rows[i].Key).Append(": ").Append(rows[i].Value[0]).Append(" textures, est ").Append(Mb(rows[i].Value[1])).Append(" MB\n");
            }
            catch (Exception e)
            {
                sb.Append("icon and format breakdown failed: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }

            string path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "FastLoad"), "census_" + Timeline.Stamp + "_" + Safe(label) + ".txt");
            File.WriteAllText(path, sb.ToString());
            return "census took " + (int)sw.Elapsed.TotalMilliseconds + "ms -> " + Path.GetFileName(path) + " | " + summary.Trim();
        }

        private static long Profile(string method)
        {
            try
            {
                MethodInfo m = typeof(UnityEngine.Profiling.Profiler).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                return ReferenceEquals(m, null) ? -1 : Convert.ToInt64(m.Invoke(null, null));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        internal static long Estimate(UnityEngine.Object o)
        {
            try
            {
                Texture2D t = o as Texture2D;
                if (t != null) return (long)t.width * t.height * BytesPerPixelX8(t.format) / 8 * (t.mipmapCount > 1 ? 4 : 3) / 3;
                RenderTexture r = o as RenderTexture;
                if (r != null) return (long)r.width * r.height * 4 * Math.Max(1, r.antiAliasing) + (r.depth > 0 ? (long)r.width * r.height * 4 : 0);
                Cubemap c = o as Cubemap;
                if (c != null) return (long)c.width * c.height * 6 * 4;
                Mesh m = o as Mesh;
                if (m != null) return (long)m.vertexCount * 56;
                AudioClip a = o as AudioClip;
                if (a != null) return a.loadType == AudioClipLoadType.DecompressOnLoad ? (long)a.samples * a.channels * 2 : (long)a.samples * a.channels / 5;
            }
            catch (Exception)
            {
            }
            return 0;
        }

        /// <summary>Bits a pixel, by format family.</summary>
        private static int BytesPerPixelX8(TextureFormat f)
        {
            string name = f.ToString();
            if (name.StartsWith("DXT1", StringComparison.Ordinal) || name == "BC4") return 4;
            if (name.StartsWith("DXT5", StringComparison.Ordinal) || name == "BC5" || name == "BC7" || name == "BC6H") return 8;
            if (name == "Alpha8" || name == "R8") return 8;
            if (name == "RGB24") return 24;
            if (name == "RGBAHalf") return 64;
            if (name == "RGBAFloat") return 128;
            return 32;
        }

        private static string Detail(UnityEngine.Object o)
        {
            try
            {
                Texture2D t = o as Texture2D;
                if (t != null) return " [" + t.width + "x" + t.height + " " + t.format + " mips=" + t.mipmapCount + "]";
                Mesh m = o as Mesh;
                if (m != null) return " [" + m.vertexCount + " verts" + (m.isReadable ? " readable" : "") + "]";
                AudioClip a = o as AudioClip;
                if (a != null) return " [" + a.length.ToString("0.0", CultureInfo.InvariantCulture) + "s " + a.channels + "ch " + a.loadType + "]";
                RenderTexture r = o as RenderTexture;
                if (r != null) return " [" + r.width + "x" + r.height + " aa" + r.antiAliasing + "]";
            }
            catch (Exception)
            {
            }
            return "";
        }

        internal static string Mb(long bytes)
        {
            return bytes < 0 ? "?" : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Safe(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '-' ? s[i] : '_');
            return sb.ToString();
        }
    }

    /// <summary>Test steps around IconGovernor.</summary>
    internal static class Icons
    {
        /// <summary>Icon textures, by asking every known sprite. READS EVERY DROPPED PICTURE BACK IN.</summary>
        public static List<Texture2D> Textures()
        {
            List<Texture2D> list = new List<Texture2D>();
            IconGovernor g = IconGovernor.Instance;
            if (g == null) return list;
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            List<Sprite> sprites = g.KnownSprites();
            for (int i = 0; i < sprites.Count; i++)
            {
                if (sprites[i] == null) continue;
                Texture2D t = sprites[i].texture;
                if (t == null || seen.ContainsKey(t.GetInstanceID())) continue;
                seen[t.GetInstanceID()] = true;
                list.Add(t);
                g.Touched(t);
            }
            return list;
        }

        public static string UnloadAll()
        {
            IconGovernor g = IconGovernor.Instance;
            return g == null ? "ITEM ICONS ON DEMAND IS OFF" : g.Pass("test step");
        }

        /// <summary>Draws a spread of the icons small and reads the result: a picture that came back has pixels.</summary>
        public static string Check()
        {
            IconGovernor g = IconGovernor.Instance;
            if (g == null) return "ITEM ICONS ON DEMAND IS OFF";
            List<Sprite> sprites = g.KnownSprites();
            if (sprites.Count == 0) return "NO ICONS KNOWN";
            int back = 0;
            int tried = 0;
            string detail = "";
            RenderTexture rt = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32);
            Texture2D read = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            RenderTexture before = RenderTexture.active;
            int stepSize = Math.Max(1, sprites.Count / 12);
            for (int i = 0; i < sprites.Count && tried < 12; i += stepSize)
            {
                if (sprites[i] == null) continue;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                Texture2D t = sprites[i].texture;
                if (t == null) continue;
                tried++;
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                Color32[] px = read.GetPixels32();
                int lit = 0;
                for (int k = 0; k < px.Length; k++) if (px[k].a > 8 || px[k].r + px[k].g + px[k].b > 24) lit++;
                if (lit > 0) back++;
                detail += (detail.Length == 0 ? "" : ", ") + t.name + " " + t.width + "x" + t.height + " id" + (t.GetInstanceID() > 0 ? "+" : "-") + " lit=" + lit + "/1024 " + (int)sw.Elapsed.TotalMilliseconds + "ms";
                g.Touched(t);
            }
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(read);
            return back + " of " + tried + " icons have pixels after being asked for and drawn: " + detail;
        }
    }
}
