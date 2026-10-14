using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using FastLoad.Early;
using HarmonyLib;
using ModularWorkshop;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Stand-ins for modular parts whose bundle is not loaded. A workshop panel lists every part of its group by
    /// name and small picture; only a part that is put on a gun needs its model. So for a bundle whose parts are
    /// known from an earlier load (name, Take and Hold settings, a 64 pixel copy of the panel picture, all kept in
    /// BepInEx/cache), each part gets an empty stand-in object in the workshop's part list. Nothing is read from
    /// the bundle until a stand-in is picked: then its bundle loads in the background, the real parts take the
    /// stand-ins' places in the list, and the pick is carried out.
    /// </summary>
    public static class MwoStubs
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        public const int Tile = 64;

        private sealed class Stub
        {
            public GameObject Go;
            public string Path;
        }

        private sealed class Job
        {
            public PartIndex.Entry Entry;
            public string Path;
            public Sprite[] Sprites;
            public int Next;
            public byte[] Atlas;
        }

        public delegate void Done();

        private static string s_dir = "";
        private static Transform s_root;
        private static readonly Dictionary<string, Stub> s_stubs = new Dictionary<string, Stub>(StringComparer.Ordinal);
        /// <summary>"path|group" for which stand-ins were made.</summary>
        private static readonly Dictionary<string, bool> s_made = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> s_atlases = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Job> s_jobs = new List<Job>();
        private static Texture2D s_read;

        private static FieldInfo s_partNames;
        private static FieldInfo s_pageIndex;
        private static FieldInfo s_showingSkins;
        private static bool s_reselecting;

        public static int Made;
        public static string LastTimes = "";
        public static int Replaced;
        public static int Picked;

        public static void Install(Harmony h)
        {
            s_dir = Path.Combine(Paths.CachePath, "FastLoad.modularpictures");
            s_partNames = typeof(ModularWorkshopUI).GetField("_partNames", Any);
            s_pageIndex = typeof(ModularWorkshopUI).GetField("_pageIndex", Any);
            s_showingSkins = typeof(ModularWorkshopUI).GetField("_isShowingSkins", Any);
            if (ReferenceEquals(s_partNames, null) || ReferenceEquals(s_pageIndex, null) || ReferenceEquals(s_showingSkins, null))
                throw new InvalidOperationException("ModularWorkshopUI has other field names than expected");
            h.Patch(typeof(ModularWorkshopUI).GetMethod("PButton_Select", Any), new HarmonyMethod(typeof(MwoStubs).GetMethod("SelectPre", Any)));
        }

        public static bool IsStub(string group, string part)
        {
            return s_stubs.ContainsKey(PartIndex.PartKey(group, part));
        }

        private static string PictureFile(string bundlePath)
        {
            // FNV-1a over the lower-case path: the same file name on every start.
            ulong hash = 14695981039346656037UL;
            string lower = bundlePath.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                hash ^= lower[i];
                hash *= 1099511628211UL;
            }
            return Path.Combine(s_dir, hash.ToString("x16") + ".rgba");
        }

        // ---- learning: the small pictures of a bundle that has just loaded

        /// <summary>Called while a bundle registers, with the panel picture of each part in the entry's list (same order).</summary>
        public static void Learn(PartIndex.Entry entry, string path, Sprite[] sprites)
        {
            if (entry == null || sprites == null || sprites.Length == 0 || sprites.Length != entry.List.Count) return;
            Job job = new Job();
            job.Entry = entry;
            job.Path = path;
            job.Sprites = sprites;
            int cols = PartIndex.PictureColumns(sprites.Length);
            int rows = PartIndex.PictureRows(sprites.Length);
            job.Atlas = new byte[cols * Tile * rows * Tile * 4];
            s_jobs.Add(job);
        }

        /// <summary>Once a frame: a few pictures of the bundle being learned. True when an entry became complete (the index should be written).</summary>
        public static bool Tick()
        {
            if (s_jobs.Count == 0) return false;
            Job job = s_jobs[0];
            try
            {
                for (int n = 0; n < 4 && job.Next < job.Sprites.Length; n++)
                {
                    Capture(job.Sprites[job.Next], job.Atlas, job.Next, job.Sprites.Length);
                    job.Next++;
                }
                if (job.Next < job.Sprites.Length) return false;
                s_jobs.RemoveAt(0);
                Directory.CreateDirectory(s_dir);
                File.WriteAllBytes(PictureFile(job.Path), job.Atlas);
                job.Entry.Pictures = job.Entry.List.Count;
                Timeline.Write("DEF", "modular parts: " + BundleLedger.NameOf(job.Path) + " can be stood in for from now on (" + job.Entry.List.Count + " parts)");
                return true;
            }
            catch (Exception e)
            {
                s_jobs.Remove(job);
                Timeline.Write("DEF", "modular parts: pictures of " + BundleLedger.NameOf(job.Path) + " not kept: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static void Capture(Sprite sprite, byte[] atlas, int index, int count)
        {
            if (s_read == null) s_read = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
            Texture2D source = sprite != null ? sprite.texture : null;
            RenderTexture rt = RenderTexture.GetTemporary(Tile, Tile, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            if (source != null)
            {
                Rect r;
                try
                {
                    r = sprite.textureRect;
                }
                catch (Exception)
                {
                    r = sprite.rect;
                }
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Tile, Tile, 0f);
                Graphics.DrawTexture(new Rect(0f, 0f, Tile, Tile), source, new Rect(r.x / source.width, r.y / source.height, r.width / source.width, r.height / source.height), 0, 0, 0, 0);
                GL.PopMatrix();
            }
            s_read.ReadPixels(new Rect(0f, 0f, Tile, Tile), 0, 0, false);
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            byte[] px = s_read.GetRawTextureData();
            int cols = PartIndex.PictureColumns(count);
            int col = index % cols;
            int row = index / cols;
            int width = cols * Tile;
            for (int y = 0; y < Tile; y++) Buffer.BlockCopy(px, y * Tile * 4, atlas, ((row * Tile + y) * width + col * Tile) * 4, Tile * 4);
        }

        // ---- stand-ins

        /// <summary>Makes stand-ins for the parts of that group held by bundles that are known and not loaded. Returns how many were made.</summary>
        public static int EnsureGroup(string group, PartIndex index)
        {
            int made = 0;
            if (string.IsNullOrEmpty(group) || index == null) return 0;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            double look = 0, sheets = 0;
            ModularWorkshopPartsDefinition def = null;
            Dictionary<string, bool> names = null;
            foreach (KeyValuePair<string, PartIndex.Entry> pair in index.Entries)
            {
                PartIndex.Entry e = pair.Value;
                if (!PartIndex.Complete(e) || !e.Groups.ContainsKey(group)) continue;
                string madeKey = pair.Key + "|" + group;
                if (s_made.ContainsKey(madeKey)) continue;
                double t0 = sw.Elapsed.TotalMilliseconds;
                bool skip = MwoParts.IsRegistered(pair.Key) || !ReferenceEquals(MwoParts.Look(pair.Key), e);
                double t1 = sw.Elapsed.TotalMilliseconds;
                look += t1 - t0;
                if (skip) continue;
                Texture2D atlas = Atlas(pair.Key, e.List.Count);
                double t2 = sw.Elapsed.TotalMilliseconds;
                sheets += t2 - t1;
                if (atlas == null) continue;
                s_made[madeKey] = true;
                if (def == null)
                {
                    if (!ModularWorkshopManager.ModularWorkshopPartsGroupsDictionary.TryGetValue(group, out def) || def == null)
                    {
                        def = ScriptableObject.CreateInstance<ModularWorkshopPartsDefinition>();
                        def.name = "FastLoad " + group;
                        def.ModularPartsGroupID = group;
                        def.DisplayName = group;
                        def.ModularPrefabs = new List<GameObject>();
                        ModularWorkshopManager.ModularWorkshopPartsGroupsDictionary[group] = def;
                    }
                    if (def.ModularPrefabs == null) def.ModularPrefabs = new List<GameObject>();
                    names = new Dictionary<string, bool>(StringComparer.Ordinal);
                    for (int i = 0; i < def.ModularPrefabs.Count; i++)
                    {
                        ModularWeaponPart p = def.ModularPrefabs[i] != null ? def.ModularPrefabs[i].GetComponent<ModularWeaponPart>() : null;
                        if (p != null && p.Name != null) names[p.Name] = true;
                    }
                }
                int cols = PartIndex.PictureColumns(e.List.Count);
                for (int i = 0; i < e.List.Count; i++)
                {
                    PartIndex.Part part = e.List[i];
                    if (part.Group != group || names.ContainsKey(part.Name)) continue;
                    names[part.Name] = true;
                    GameObject go;
                    string poolKey = pair.Key + "|" + i;
                    if (s_pool.TryGetValue(poolKey, out go) && go != null) s_pool.Remove(poolKey);
                    else go = MakeStandIn(part, atlas, i, cols);
                    def.ModularPrefabs.Add(go);
                    Stub s = new Stub();
                    s.Go = go;
                    s.Path = pair.Key;
                    s_stubs[PartIndex.PartKey(group, part.Name)] = s;
                    made++;
                }
            }
            Made += made;
            LastTimes = "file checks " + (int)look + " ms, picture sheets " + (int)sheets + " ms, objects " + (int)(sw.Elapsed.TotalMilliseconds - look - sheets) + " ms";
            return made;
        }

        /// <summary>Stand-ins made ahead of time, by "bundle path|part number": a panel then only hooks them up.</summary>
        private static readonly Dictionary<string, GameObject> s_pool = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static List<KeyValuePair<string, PartIndex.Entry>> s_prepList;
        private static int s_prepEntry;
        private static int s_prepPart;
        private static readonly System.Diagnostics.Stopwatch s_prepWatch = new System.Diagnostics.Stopwatch();
        public static int Prepared;

        private static GameObject MakeStandIn(PartIndex.Part part, Texture2D atlas, int i, int cols)
        {
            if (s_root == null)
            {
                GameObject root = new GameObject("FastLoad modular stand-ins");
                root.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(root);
                s_root = root.transform;
            }
            GameObject go = new GameObject("FastLoad stand-in " + part.Name);
            go.SetActive(false);
            go.transform.SetParent(s_root, false);
            // On an object that is off: the part's own start-up code never runs.
            ModularWeaponPart stand = go.AddComponent<ModularWeaponPart>();
            stand.Name = part.Name;
            stand.Icon = Sprite.Create(atlas, new Rect((i % cols) * Tile, (i / cols) * Tile, Tile, Tile), new Vector2(0.5f, 0.5f));
            stand.RemovedFromSpawnTable = part.NoSpawn;
            stand.AdditionalSpawnTableEntries = part.ExtraSpawn;
            stand.AttachmentMounts = new FistVR.FVRFireArmAttachmentMount[0];
            stand.SubAttachmentPoints = new ModularWeaponPartsAttachmentPoint[0];
            stand.SubQuickBeltSlots = new FistVR.FVRQuickBeltSlot[0];
            stand.MeshRenderers = new MeshRenderer[0];
            stand.ParentToFirearm = new Transform[0];
            stand.AlsoOccupiesPointWithModularPartsGroupIDs = new string[0];
            return go;
        }

        /// <summary>Once a frame: makes stand-ins ahead of time for every known bundle, about 2 ms a frame, so no panel has to.</summary>
        public static void Prepare(PartIndex index)
        {
            if (index == null) return;
            if (s_prepList == null)
            {
                s_prepList = new List<KeyValuePair<string, PartIndex.Entry>>(index.Entries);
                s_prepEntry = 0;
                s_prepPart = 0;
            }
            if (s_prepEntry >= s_prepList.Count) return;
            s_prepWatch.Reset();
            s_prepWatch.Start();
            while (s_prepEntry < s_prepList.Count && s_prepWatch.Elapsed.TotalMilliseconds < 2.0)
            {
                KeyValuePair<string, PartIndex.Entry> pair = s_prepList[s_prepEntry];
                PartIndex.Entry e = pair.Value;
                if (s_prepPart == 0 && (!PartIndex.Complete(e) || MwoParts.IsRegistered(pair.Key) || !ReferenceEquals(MwoParts.Look(pair.Key), e)))
                {
                    s_prepEntry++;
                    continue;
                }
                Texture2D atlas = Atlas(pair.Key, e.List.Count);
                if (atlas == null || s_prepPart >= e.List.Count)
                {
                    s_prepEntry++;
                    s_prepPart = 0;
                    continue;
                }
                string key = pair.Key + "|" + s_prepPart;
                if (!s_pool.ContainsKey(key))
                {
                    s_pool[key] = MakeStandIn(e.List[s_prepPart], atlas, s_prepPart, PartIndex.PictureColumns(e.List.Count));
                    Prepared++;
                }
                s_prepPart++;
            }
            if (s_prepEntry >= s_prepList.Count) Timeline.Write("DEF", "modular parts: " + Prepared + " stand-ins made ahead of time");
        }

        private static Texture2D Atlas(string bundlePath, int parts)
        {
            Texture2D atlas;
            if (s_atlases.TryGetValue(bundlePath, out atlas) && atlas != null) return atlas;
            try
            {
                int width = PartIndex.PictureColumns(parts) * Tile;
                int height = PartIndex.PictureRows(parts) * Tile;
                string file = PictureFile(bundlePath);
                if (!File.Exists(file)) return null;
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length != width * height * 4) return null;
                atlas = new Texture2D(width, height, TextureFormat.RGBA32, false);
                atlas.name = "FastLoad pictures " + BundleLedger.NameOf(bundlePath);
                atlas.LoadRawTextureData(bytes);
                atlas.Apply(false, false);
                s_atlases[bundlePath] = atlas;
                return atlas;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Real parts have registered: each takes its stand-in's place in the group's list.</summary>
        public static void Registered(UnityEngine.Object[] assets)
        {
            if (assets == null || s_stubs.Count == 0) return;
            for (int a = 0; a < assets.Length; a++)
            {
                ModularWorkshopPartsDefinition def = assets[a] as ModularWorkshopPartsDefinition;
                if (def == null || def.ModularPrefabs == null || string.IsNullOrEmpty(def.ModularPartsGroupID)) continue;
                ModularWorkshopPartsDefinition groupDef;
                if (!ModularWorkshopManager.ModularWorkshopPartsGroupsDictionary.TryGetValue(def.ModularPartsGroupID, out groupDef) || groupDef == null) continue;
                List<GameObject> list = groupDef.ModularPrefabs;
                GameObject[] real = def.ModularPrefabs.ToArray();
                for (int i = 0; i < real.Length; i++)
                {
                    ModularWeaponPart part = real[i] != null ? real[i].GetComponent<ModularWeaponPart>() : null;
                    if (part == null || part.Name == null) continue;
                    string key = PartIndex.PartKey(def.ModularPartsGroupID, part.Name);
                    Stub stub;
                    if (!s_stubs.TryGetValue(key, out stub)) continue;
                    s_stubs.Remove(key);
                    int at = list.IndexOf(stub.Go);
                    if (at >= 0)
                    {
                        int own = list.IndexOf(real[i]);
                        if (own < 0) list[at] = real[i];
                        else if (ReferenceEquals(groupDef, def)) list.RemoveAt(at);
                        else
                        {
                            // The loader appended it at the end: it goes where its stand-in was, so panels keep their order.
                            list[at] = real[i];
                            list.RemoveAt(own);
                        }
                    }
                    if (stub.Go != null) UnityEngine.Object.Destroy(stub.Go);
                    Replaced++;
                }
            }
        }

        // ---- picking a stand-in on a panel

        private static bool SelectPre(ModularWorkshopUI __instance, int i)
        {
            try
            {
                if (s_reselecting || __instance == null || (bool)s_showingSkins.GetValue(__instance)) return true;
                string[] names = s_partNames.GetValue(__instance) as string[];
                int index = i + (int)s_pageIndex.GetValue(__instance) * __instance.PartButtons.Length;
                if (names == null || index < 0 || index >= names.Length) return true;
                string name = names[index];
                Stub stub;
                if (!s_stubs.TryGetValue(PartIndex.PartKey(__instance.ModularPartsGroupID, name), out stub)) return true;
                ModularWorkshopUI panel = __instance;
                string group = __instance.ModularPartsGroupID;
                Timeline.Write("DEF", "modular parts: '" + name + "' picked on the '" + group + "' panel; loading " + BundleLedger.NameOf(stub.Path));
                List<string> one = new List<string>();
                one.Add(stub.Path);
                MwoParts.LoadThen(one, delegate { Reselect(panel, group, name); });
                __instance.Beep();
                return false;
            }
            catch (Exception e)
            {
                Timeline.Write("DEF", "modular parts: stand-in pick failed (" + e.GetType().Name + ": " + e.Message + "); left to the workshop");
                return true;
            }
        }

        private static void Reselect(ModularWorkshopUI panel, string group, string name)
        {
            if (panel == null) return;
            if (IsStub(group, name))
            {
                Timeline.Write("DEF", "modular parts: '" + name + "' is still a stand-in after its bundle loaded: NOT put on");
                return;
            }
            panel.InitializeArrays();
            string[] names = s_partNames.GetValue(panel) as string[];
            int index = names == null ? -1 : Array.IndexOf(names, name);
            if (index < 0)
            {
                panel.UpdateDisplay();
                return;
            }
            int perPage = panel.PartButtons.Length;
            s_pageIndex.SetValue(panel, index / perPage);
            s_reselecting = true;
            try
            {
                panel.PButton_Select(index % perPage);
                Picked++;
            }
            finally
            {
                s_reselecting = false;
            }
        }

        // ---- for the test driver

        public static string Report()
        {
            Dictionary<string, int> perGroup = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in s_stubs.Keys)
            {
                string group = key.Substring(0, key.IndexOf('|'));
                int n;
                perGroup.TryGetValue(group, out n);
                perGroup[group] = n + 1;
            }
            string text = s_stubs.Count + " stand-ins now (" + Made + " made, " + Replaced + " replaced by real parts, " + Picked + " picks carried out), " + s_atlases.Count + " picture sheets";
            foreach (KeyValuePair<string, int> pair in perGroup) text += "; " + pair.Key + "=" + pair.Value;
            string dir = Path.Combine(Paths.BepInExRootPath, "FastLoad");
            int written = 0;
            foreach (KeyValuePair<string, Texture2D> pair in s_atlases)
            {
                if (written >= 3 || pair.Value == null) break;
                File.WriteAllBytes(Path.Combine(dir, "pictures_" + BundleLedger.NameOf(pair.Key) + ".png"), pair.Value.EncodeToPNG());
                written++;
            }
            return text + "; " + written + " picture sheets written as PNG";
        }

        private static ModularWorkshopUI s_testPanel;
        private static string s_testGroup;
        private static string s_testName;

        /// <summary>"group text": presses the button of the first stand-in in that group's panel whose name contains the text ("*" = any).</summary>
        public static string TestPick(string rest)
        {
            int space = rest.IndexOf(' ');
            string group = space < 0 ? rest : rest.Substring(0, space);
            string text = space < 0 ? "*" : rest.Substring(space + 1).Trim();
            s_testPanel = null;
            ModularWorkshopUI[] panels = UnityEngine.Object.FindObjectsOfType<ModularWorkshopUI>();
            for (int p = 0; p < panels.Length; p++)
            {
                if (panels[p].ModularPartsGroupID != group) continue;
                string[] names = s_partNames.GetValue(panels[p]) as string[];
                if (names == null) continue;
                for (int i = 0; i < names.Length; i++)
                {
                    if (!IsStub(group, names[i]) || (text != "*" && names[i].IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    int perPage = panels[p].PartButtons.Length;
                    s_pageIndex.SetValue(panels[p], i / perPage);
                    s_testPanel = panels[p];
                    s_testGroup = group;
                    s_testName = names[i];
                    panels[p].PButton_Select(i % perPage);
                    return "pressed '" + names[i] + "' (" + (i + 1) + " of " + names.Length + " parts) on the '" + group + "' panel";
                }
                return "NO STAND-IN matching '" + text + "' among the " + names.Length + " parts of the '" + group + "' panel";
            }
            return "NO PANEL for group '" + group + "' (" + panels.Length + " panels in the scene)";
        }

        /// <summary>Name of the part now on the gun at the tested panel's point, when the pressed part is on it; null while waiting.</summary>
        public static string TestPicked()
        {
            if (s_testPanel == null) return "panel gone";
            ModularWeaponPartsAttachmentPoint point = null;
            if (s_testPanel.ModularWeapon != null) s_testPanel.ModularWeapon.AllAttachmentPoints.TryGetValue(s_testGroup, out point);
            else if (s_testPanel.ModularPhysicalObject != null) s_testPanel.ModularPhysicalObject.AllAttachmentPoints.TryGetValue(s_testGroup, out point);
            if (point == null || point.SelectedModularWeaponPart != s_testName) return null;
            ModularWeaponPart on = point.ModularPartPoint != null ? point.ModularPartPoint.GetComponent<ModularWeaponPart>() : null;
            if (on == null) return "'" + s_testName + "' selected but NO PART OBJECT at the point";
            bool stand = on.name.IndexOf("FastLoad stand-in", StringComparison.Ordinal) >= 0;
            int meshes = on.MeshRenderers != null ? on.MeshRenderers.Length : -1;
            return "'" + s_testName + "' is on the gun as " + (stand ? "A STAND-IN (wrong)" : "the real part") + " '" + on.name + "', " + meshes + " mesh renderers";
        }
    }
}
