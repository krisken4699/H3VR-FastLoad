using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using FastLoad.Early;
using FistVR;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Measuring steps for the test driver, about what stops the game while modular content loads:
    ///   bundleprobe name [prefabs]   one part pack, opened and read piece by piece: the file alone, single part
    ///                                prefabs by name, then everything. Each piece: time, longest frame, memory.
    ///   bench seconds                puts the spawned gun on the spawned workshop platform and watches the frames.
    /// Nothing here runs outside a test script.
    /// </summary>
    public static class LoadLab
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static readonly List<string> Lines = new List<string>();
        public static string Note = "";

        private static string s_kind;
        private static readonly Stopwatch s_frame = new Stopwatch();
        private static readonly Stopwatch s_phase = new Stopwatch();
        private static double s_worst;
        private static int s_slow;
        private static double s_slowMs;
        private static int s_frames;
        private static long s_commit0;
        private static int s_tex0;

        // bundleprobe
        private static string s_path;
        private static int s_stage;
        private static AssetBundleCreateRequest s_open;
        private static AssetBundle s_bundle;
        private static AssetBundleRequest s_request;
        private static readonly List<string> s_prefabs = new List<string>();
        private static int s_prefabIndex;
        private static readonly List<string> s_assets = new List<string>();
        private static int s_assetIndex;
        private static readonly Dictionary<string, long[]> s_byType = new Dictionary<string, long[]>();
        private static int s_prefabCount;
        private static string s_phaseName;

        // bench
        private static double s_seconds;
        private static readonly Stopwatch s_total = new Stopwatch();

        public static void Begin(string kind, string rest, List<GameObject> made)
        {
            s_kind = kind;
            Lines.Clear();
            Note = "";
            s_stage = 0;
            if (kind == "bundleprobe") BeginProbe(rest);
            else if (kind == "pick") BeginPick(rest);
            else BeginBench(rest, made);
        }

        /// <summary>Once a frame; true when the step is over.</summary>
        public static bool Tick()
        {
            double ms = s_frame.IsRunning ? s_frame.Elapsed.TotalMilliseconds : 0.0;
            s_frame.Reset();
            s_frame.Start();
            s_frames++;
            if (ms > s_worst) s_worst = ms;
            if (ms > 50.0)
            {
                s_slow++;
                s_slowMs += ms;
            }
            if (s_stage < 0) return true;
            return s_kind == "bundleprobe" ? TickProbe() : s_kind == "pick" ? TickPick() : TickBench();
        }

        private static void StartPhase(string name)
        {
            s_phaseName = name;
            s_tex0 = Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length;
            s_commit0 = Memory.Commit();
            s_worst = 0.0;
            s_slow = 0;
            s_slowMs = 0.0;
            s_frames = 0;
            s_phase.Reset();
            s_phase.Start();
            s_frame.Reset();
            s_frame.Start();
        }

        private static void EndPhase(string extra)
        {
            double wall = s_phase.Elapsed.TotalMilliseconds;
            int tex = Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length;
            Lines.Add("lab " + s_phaseName + ": " + (int)wall + " ms, " + s_frames + " frames, longest frame " + (int)s_worst + " ms, "
                + s_slow + " frames over 50 ms (" + (int)s_slowMs + " ms in them), commit " + Signed((Memory.Commit() - s_commit0) / 1048576) + " MB, textures "
                + Signed(tex - s_tex0) + (extra.Length == 0 ? "" : ", " + extra));
        }

        private static string Signed(long n)
        {
            return (n >= 0 ? "+" : "") + n;
        }

        // ---- bundleprobe

        private static void BeginProbe(string rest)
        {
            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            s_prefabCount = 3;
            if (parts.Length > 1) int.TryParse(parts[1], out s_prefabCount);
            s_path = null;
            s_bundle = null;
            s_prefabs.Clear();
            s_prefabIndex = 0;
            s_assets.Clear();
            s_assetIndex = 0;
            s_byType.Clear();
            try
            {
                string[] found = parts.Length == 0 ? new string[0] : Directory.GetFiles(Paths.PluginPath, parts[0], SearchOption.AllDirectories);
                if (found.Length > 0) s_path = found[0];
            }
            catch (Exception)
            {
            }
            if (s_path == null)
            {
                Note = "NO FILE named '" + rest + "' under plugins";
                s_stage = -1;
                return;
            }
            Lines.Add("lab file " + s_path + " " + (new FileInfo(s_path).Length / 1048576) + " MB, background loading " + Application.backgroundLoadingPriority);
            StartPhase("open file");
            s_open = AssetBundle.LoadFromFileAsync(s_path);
            s_stage = 1;
        }

        private static bool TickProbe()
        {
            if (s_stage == 1)
            {
                if (!s_open.isDone) return false;
                s_bundle = s_open.assetBundle;
                if (s_bundle == null)
                {
                    Note = "file did not open (already loaded by something else?)";
                    s_stage = -1;
                    return true;
                }
                string[] names = s_bundle.GetAllAssetNames();
                int assets = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) s_prefabs.Add(names[i]);
                    else if (names[i].EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        assets++;
                        s_assets.Add(names[i]);
                    }
                }
                EndPhase(names.Length + " named assets: " + s_prefabs.Count + " prefabs, " + assets + " .asset");
                s_stage = 2;
                return false;
            }
            if (s_stage == 2)
            {
                if (s_prefabIndex >= s_prefabCount || s_prefabIndex >= s_prefabs.Count)
                {
                    if (s_assetIndex < s_assets.Count)
                    {
                        StartPhase("asset " + Path.GetFileName(s_assets[s_assetIndex]));
                        s_request = s_bundle.LoadAssetAsync(s_assets[s_assetIndex]);
                        s_stage = 6;
                        return false;
                    }
                    foreach (KeyValuePair<string, long[]> pair in s_byType)
                        Lines.Add("lab by type " + pair.Key + ": " + pair.Value[0] + " assets, " + pair.Value[1] + " ms, commit +" + pair.Value[2] + " MB, textures +" + pair.Value[3]
                            + ", longest frame " + pair.Value[4] + " ms");
                    StartPhase("everything left (LoadAllAssetsAsync)");
                    s_request = s_bundle.LoadAllAssetsAsync();
                    s_stage = 4;
                    return false;
                }
                // Spread over the list: first, middle, last.
                int pick = s_prefabCount <= 1 ? 0 : (int)((long)s_prefabIndex * (s_prefabs.Count - 1) / Math.Max(1, Math.Min(s_prefabCount, s_prefabs.Count) - 1));
                string name = s_prefabs[pick];
                StartPhase("one prefab " + Path.GetFileName(name));
                s_request = s_bundle.LoadAssetAsync(name, typeof(GameObject));
                s_stage = 3;
                return false;
            }
            if (s_stage == 3)
            {
                if (!s_request.isDone) return false;
                GameObject go = s_request.asset as GameObject;
                int renderers = go != null ? go.GetComponentsInChildren<Renderer>(true).Length : -1;
                EndPhase(go == null ? "NOT A GAMEOBJECT" : renderers + " renderers");
                s_prefabIndex++;
                s_stage = 2;
                return false;
            }
            if (s_stage == 6)
            {
                if (!s_request.isDone) return false;
                UnityEngine.Object o = s_request.asset;
                string type = o == null ? "null" : o.GetType().Name;
                long[] sum;
                if (!s_byType.TryGetValue(type, out sum)) s_byType[type] = sum = new long[5];
                int tex = Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length - s_tex0;
                long mb = (Memory.Commit() - s_commit0) / 1048576;
                sum[0]++;
                sum[1] += (long)s_phase.Elapsed.TotalMilliseconds;
                sum[2] += mb;
                sum[3] += tex;
                if (s_worst > sum[4]) sum[4] = (long)s_worst;
                // One line each only for what is big: the rest is in the sums.
                if (mb >= 40 || s_worst > 50.0 || type != "ModularWorkshopSkinsDefinition") EndPhase(type);
                s_assetIndex++;
                s_stage = 2;
                return false;
            }
            if (s_stage == 4)
            {
                if (!s_request.isDone) return false;
                EndPhase(s_request.allAssets.Length + " objects");
                StartPhase("unload");
                s_bundle.Unload(true);
                s_bundle = null;
                s_stage = 5;
                return false;
            }
            EndPhase("");
            Note = Lines.Count + " lines";
            s_stage = -1;
            return true;
        }

        // ---- pick: a stand-in pressed on a workshop panel, until the real part is on the gun

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static string VaultPart(string rest, List<GameObject> made)
        {
            return MwoStubs.TestConfigure(rest, made);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static string StandIns()
        {
            return MwoStubs.Report();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void BeginPick(string rest)
        {
            StartPhase("pick " + rest);
            s_total.Reset();
            s_total.Start();
            string said = MwoStubs.TestPick(rest);
            Lines.Add("lab " + said);
            if (said.StartsWith("NO ", StringComparison.Ordinal))
            {
                Note = said;
                s_stage = -1;
                return;
            }
            s_stage = 1;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TickPick()
        {
            string on = MwoStubs.TestPicked();
            if (on == null && s_total.Elapsed.TotalSeconds < 90.0) return false;
            EndPhase(on ?? "NOT ON THE GUN after 90 s");
            Note = on ?? "NOT ON THE GUN after 90 s";
            s_stage = -1;
            return true;
        }

        // ---- bench

        private static void BeginBench(string rest, List<GameObject> made)
        {
            s_seconds = 60.0;
            double.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s_seconds);
            if (s_seconds <= 0.0) s_seconds = 60.0;
            FVRQuickBeltSlot slot = null;
            FVRPhysicalObject gun = null;
            string platformName = "";
            for (int i = 0; i < made.Count; i++)
            {
                GameObject go = made[i];
                if (go == null) continue;
                MonoBehaviour[] all = go.GetComponentsInChildren<MonoBehaviour>(true);
                bool isPlatform = false;
                for (int k = 0; k < all.Length; k++)
                {
                    if (all[k] == null || all[k].GetType().Name != "ModularWorkshopPlatform") continue;
                    FieldInfo f = all[k].GetType().GetField("QuickBeltSlot", Any);
                    slot = ReferenceEquals(f, null) ? null : f.GetValue(all[k]) as FVRQuickBeltSlot;
                    platformName = go.name;
                    isPlatform = true;
                    break;
                }
                if (!isPlatform && gun == null)
                {
                    FVRFireArm arm = go.GetComponent<FVRFireArm>();
                    if (arm != null) gun = arm;
                }
            }
            if (slot == null || gun == null)
            {
                Note = "NEEDS a spawned workshop platform (" + (slot == null ? "none found" : platformName) + ") and a spawned gun (" + (gun == null ? "none found" : gun.name) + ")";
                s_stage = -1;
                return;
            }
            Lines.Add("lab bench: " + gun.name + " onto " + platformName);
            StartPhase("gun on the platform, " + (int)s_seconds + " s");
            s_total.Reset();
            s_total.Start();
            gun.SetQuickBeltSlot(slot);
            s_stage = 1;
        }

        private static bool TickBench()
        {
            if (s_total.Elapsed.TotalSeconds < s_seconds) return false;
            int panels = 0;
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].GetType().Name == "ModularWorkshopUI") panels++;
            EndPhase(panels + " workshop panels in the scene");
            Note = Lines.Count + " lines";
            s_stage = -1;
            return true;
        }
    }
}
