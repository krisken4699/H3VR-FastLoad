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
                    else if (verb == "profile") m_step = new ProfileStep(rest);
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
