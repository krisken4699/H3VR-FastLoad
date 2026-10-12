using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using HarmonyLib;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// "[OnDemand] SosigVoices": Custom Sosig Loader reads every installed voice pack when item loading finishes.
    /// The .wav files are read and converted on the main thread, one after another (a 7.3 s stand-still on the
    /// test profile: 851 files), and every .ogg file gets its own web-style request, dozens of folders at once
    /// (10,927 files; the game crawled at 5-24 fps for the next half minute). 619 MB of files, for sosigs that may
    /// never appear.
    ///
    /// Here nothing is read at start. A voice pack is read the first time a sosig that uses it is made:
    /// .wav files are read and converted on a second thread (with the mod's own converter) and turned into clips
    /// on the main thread within a per-frame budget; .ogg files are requested one at a time. The sosig starts
    /// talking as its lines come in, which is how the mod's own .ogg loading always behaved.
    ///
    /// Everything is found by name at run time; a version with other names is left untouched.
    /// </summary>
    public sealed class SosigVoices : MonoBehaviour
    {
        public ManualLogSource Log;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private const string AssemblyName = "Sosig_Squad.CustomSosigLoader";

        private sealed class Folder
        {
            public List<AudioClip> List;
            public string[] Wavs;
            public string[] Oggs;
            public bool Started;
        }

        private sealed class Decoded
        {
            public List<AudioClip> List;
            public float[] Data;
            public int Channels;
            public int Frequency;
        }

        private static SosigVoices s_me;
        private static readonly List<Folder> s_folders = new List<Folder>();
        private static MethodInfo[] s_convert = new MethodInfo[5];

        // Second thread: files in, samples out.
        private static readonly object s_gate = new object();
        private static readonly Queue<KeyValuePair<string, List<AudioClip>>> s_wavIn = new Queue<KeyValuePair<string, List<AudioClip>>>();
        private static readonly Queue<Decoded> s_wavOut = new Queue<Decoded>();
        private static Thread s_worker;
        private static int s_wavPending;

        private readonly Queue<KeyValuePair<string, List<AudioClip>>> m_oggs = new Queue<KeyValuePair<string, List<AudioClip>>>();
        private WWW m_www;
        private List<AudioClip> m_wwwList;
        private int m_feed;
        private int m_clips;
        private int m_failed;
        private readonly System.Diagnostics.Stopwatch m_since = new System.Diagnostics.Stopwatch();

        public static string Mode = "off";

        /// <summary>Call once after Log is set.</summary>
        public void Begin()
        {
            s_me = this;
            Mode = "waiting for Custom Sosig Loader";
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsIt(a))
                {
                    Install(a);
                    return;
                }
            }
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (ReferenceEquals(args.LoadedAssembly, null) || !IsIt(args.LoadedAssembly)) return;
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                Install(args.LoadedAssembly);
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>The method of that name that has a parameter of that name (there can be several of one name).</summary>
        private static MethodInfo Find(Type type, string name, string parameter)
        {
            if (ReferenceEquals(type, null)) return null;
            foreach (MethodInfo m in type.GetMethods(Any))
            {
                if (m.Name != name) continue;
                foreach (ParameterInfo p in m.GetParameters()) if (p.Name == parameter) return m;
            }
            return null;
        }

        /// <summary>By what is in it, not by its file or assembly name (those differ).</summary>
        private static bool IsIt(Assembly a)
        {
            try
            {
                string name = a.GetName().Name;
                if (name.IndexOf("CustomSosigLoader", StringComparison.OrdinalIgnoreCase) < 0) return false;
                return !ReferenceEquals(a.GetType("CustomSosigLoader.Global"), null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Install(Assembly a)
        {
            try
            {
                Type global = a.GetType("CustomSosigLoader.Global");
                Type wav = null;
                foreach (Type t in a.GetTypes()) if (t.Name == "WavUtility") wav = t;
                MethodInfo add = Find(global, "AddAllAudioFromDirectory", "path");
                MethodInfo modify = null;
                foreach (MethodInfo m in global.GetMethods(Any))
                {
                    if (m.Name != "ModifySosig") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length > 0 && ReferenceEquals(ps[0].ParameterType, typeof(Sosig))) modify = m;
                }
                if (!ReferenceEquals(wav, null))
                {
                    s_convert[1] = wav.GetMethod("Convert8BitByteArrayToAudioClipData", Any);
                    s_convert[2] = wav.GetMethod("Convert16BitByteArrayToAudioClipData", Any);
                    s_convert[3] = wav.GetMethod("Convert24BitByteArrayToAudioClipData", Any);
                    s_convert[4] = wav.GetMethod("Convert32BitByteArrayToAudioClipData", Any);
                }
                if (ReferenceEquals(add, null) || ReferenceEquals(modify, null) || ReferenceEquals(s_convert[1], null) || ReferenceEquals(s_convert[2], null)
                    || ReferenceEquals(s_convert[3], null) || ReferenceEquals(s_convert[4], null))
                {
                    Mode = "Custom Sosig Loader has other names than expected (another version?): it reads every voice pack at start, as without this [global="
                        + !ReferenceEquals(global, null) + " wav=" + !ReferenceEquals(wav, null) + " add=" + !ReferenceEquals(add, null) + " modify=" + !ReferenceEquals(modify, null)
                        + " c8=" + !ReferenceEquals(s_convert[1], null) + " c16=" + !ReferenceEquals(s_convert[2], null) + " c24=" + !ReferenceEquals(s_convert[3], null)
                        + " c32=" + !ReferenceEquals(s_convert[4], null) + " in " + a.GetName().Name + "]";
                    Log.LogWarning("Sosig voices on demand: " + Mode);
                    return;
                }
                Harmony h = new Harmony("invent60.h3vr.fastload.sosigvoices");
                h.Patch(add, new HarmonyMethod(typeof(SosigVoices).GetMethod("AddPre", Any)));
                h.Patch(modify, null, new HarmonyMethod(typeof(SosigVoices).GetMethod("ModifyPost", Any)));
                Mode = "on";
                Timeline.Write("DEF", "sosig voices on demand: patched " + a.GetName().Name);
                Log.LogInfo("Sosig voices on demand: on");
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
                Log.LogWarning("Sosig voices on demand: " + Mode);
            }
        }

        /// <summary>The mod asks for a folder's clips: the file names are noted, nothing is read.</summary>
        private static bool AddPre(string path, List<AudioClip> list)
        {
            // Listing throws for a missing folder, as it did in the mod's own code.
            Folder f = new Folder();
            f.List = list;
            f.Wavs = Directory.GetFiles(path, "*.wav", SearchOption.TopDirectoryOnly);
            f.Oggs = Directory.GetFiles(path, "*.ogg", SearchOption.TopDirectoryOnly);
            if (f.Wavs.Length > 0 || f.Oggs.Length > 0) s_folders.Add(f);
            return false;
        }

        /// <summary>A custom sosig was just set up: whatever voice it got is wanted now.</summary>
        private static void ModifyPost(Sosig __0)
        {
            try
            {
                Sosig sosig = __0;
                SosigSpeechSet s = sosig != null ? sosig.Speech : null;
                if (s == null || s_folders.Count == 0 || s_me == null) return;
                s_me.Want(s.OnWander);
                s_me.Want(s.OnSkirmish);
                s_me.Want(s.OnReloading);
                s_me.Want(s.OnInvestigate);
                s_me.Want(s.OnJointSlice);
                s_me.Want(s.OnJointSever);
                s_me.Want(s.OnJointBreak);
                s_me.Want(s.OnPain);
                s_me.Want(s.OnNeckBreak);
                s_me.Want(s.OnBackBreak);
            }
            catch (Exception)
            {
            }
        }

        private void Want(List<AudioClip> list)
        {
            if (list == null) return;
            for (int i = 0; i < s_folders.Count; i++)
            {
                Folder f = s_folders[i];
                if (f.Started || !ReferenceEquals(f.List, list)) continue;
                f.Started = true;
                Monitor.Enter(s_gate);
                try
                {
                    for (int k = 0; k < f.Wavs.Length; k++) s_wavIn.Enqueue(new KeyValuePair<string, List<AudioClip>>(f.Wavs[k], list));
                    s_wavPending += f.Wavs.Length;
                }
                finally
                {
                    Monitor.Exit(s_gate);
                }
                for (int k = 0; k < f.Oggs.Length; k++) m_oggs.Enqueue(new KeyValuePair<string, List<AudioClip>>(f.Oggs[k], list));
                if (f.Wavs.Length > 0 && s_worker == null)
                {
                    s_worker = new Thread(Work);
                    s_worker.IsBackground = true;
                    s_worker.Name = "FastLoad sosig voices";
                    s_worker.Start();
                }
                if (m_feed == 0)
                {
                    m_feed = LoadFeed.Begin("sosig voice lines", "", false, false, Clock.NowMs);
                    m_clips = 0;
                    m_failed = 0;
                    m_since.Reset();
                    m_since.Start();
                }
            }
        }

        /// <summary>Second thread: read a .wav and convert its samples with the mod's own converter.</summary>
        private static void Work()
        {
            while (true)
            {
                KeyValuePair<string, List<AudioClip>> job = default(KeyValuePair<string, List<AudioClip>>);
                bool have = false;
                Monitor.Enter(s_gate);
                try
                {
                    if (s_wavIn.Count > 0)
                    {
                        job = s_wavIn.Dequeue();
                        have = true;
                    }
                }
                finally
                {
                    Monitor.Exit(s_gate);
                }
                if (!have)
                {
                    Thread.Sleep(50);
                    continue;
                }
                Decoded d = new Decoded();
                d.List = job.Value;
                try
                {
                    // The same reading of the header as the mod's WavUtility.ToAudioClip.
                    byte[] bytes = File.ReadAllBytes(job.Key);
                    int fmtSize = BitConverter.ToInt32(bytes, 16);
                    d.Channels = BitConverter.ToUInt16(bytes, 22);
                    d.Frequency = BitConverter.ToInt32(bytes, 24);
                    int depth = BitConverter.ToUInt16(bytes, 34);
                    int dataAt = 20 + fmtSize + 4;
                    int dataSize = BitConverter.ToInt32(bytes, dataAt);
                    MethodInfo convert = depth == 8 ? s_convert[1] : depth == 16 ? s_convert[2] : depth == 24 ? s_convert[3] : depth == 32 ? s_convert[4] : null;
                    if (!ReferenceEquals(convert, null)) d.Data = convert.Invoke(null, new object[] { bytes, dataAt, dataSize }) as float[];
                }
                catch (Exception)
                {
                    d.Data = null;
                }
                Monitor.Enter(s_gate);
                try
                {
                    s_wavOut.Enqueue(d);
                }
                finally
                {
                    Monitor.Exit(s_gate);
                }
            }
        }

        private void Update()
        {
            if (m_feed == 0) return;
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                // Converted .wav samples become clips, a few a frame.
                while (sw.Elapsed.TotalMilliseconds < Unloading.FrameBudgetMs)
                {
                    Decoded d = null;
                    Monitor.Enter(s_gate);
                    try
                    {
                        if (s_wavOut.Count > 0)
                        {
                            d = s_wavOut.Dequeue();
                            s_wavPending--;
                        }
                    }
                    finally
                    {
                        Monitor.Exit(s_gate);
                    }
                    if (d == null) break;
                    if (d.Data == null || d.Data.Length == 0 || d.Channels <= 0 || d.Frequency <= 0)
                    {
                        m_failed++;
                        continue;
                    }
                    // Same call as the mod makes.
                    AudioClip clip = AudioClip.Create("wav", d.Data.Length, d.Channels, d.Frequency, false);
                    clip.SetData(d.Data, 0);
                    d.List.Add(clip);
                    m_clips++;
                }

                // One .ogg request at a time.
                if (m_www != null && m_www.isDone)
                {
                    try
                    {
                        AudioClip clip = string.IsNullOrEmpty(m_www.error) ? m_www.GetAudioClip(true, true, AudioType.OGGVORBIS) : null;
                        if (clip != null)
                        {
                            m_wwwList.Add(clip);
                            m_clips++;
                        }
                        else m_failed++;
                    }
                    finally
                    {
                        m_www.Dispose();
                        m_www = null;
                    }
                }
                if (m_www == null && m_oggs.Count > 0)
                {
                    KeyValuePair<string, List<AudioClip>> next = m_oggs.Dequeue();
                    m_wwwList = next.Value;
                    m_www = new WWW("file:///" + next.Key);
                }

                if (m_www == null && m_oggs.Count == 0 && s_wavPending <= 0)
                {
                    LoadFeed.End(m_feed, Clock.NowMs);
                    m_feed = 0;
                    Timeline.Write("DEF", "sosig voices on demand: " + m_clips + " lines read in " + (int)m_since.Elapsed.TotalSeconds + " s, " + m_failed + " could not be read");
                }
            }
            catch (Exception e)
            {
                LoadFeed.End(m_feed, Clock.NowMs);
                m_feed = 0;
                Log.LogError("Sosig voices on demand: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
