using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Test tool: where the main thread's frame goes, by phase. Two components run first and last in each of Unity's
    /// script phases ([DefaultExecutionOrder]), and the cameras' pre-cull / post-render callbacks mark rendering:
    ///   fixed scripts  - every FixedUpdate method (game and mods), all physics steps of the frame
    ///   physics        - from the last FixedUpdate method to the next phase: PhysX simulation, collision callbacks
    ///   update scripts - every Update method
    ///   animation      - between Update and LateUpdate: animators, particle and other internal updates
    ///   late scripts   - every LateUpdate method
    ///   render         - first camera pre-cull to last camera post-render: culling and drawing, CPU side
    ///   other / wait   - the rest up to the next frame: VR present and pose wait, GPU sync, GC
    /// Only switched on by the profile test step.
    /// </summary>
    public static class FramePhases
    {
        public const int Fixed = 0, Physics = 1, Update = 2, Anim = 3, Late = 4, Render = 5, Other = 6;
        private static readonly string[] Names = { "fixed scripts", "physics", "update scripts", "animation", "late scripts", "render", "other / wait" };

        private static readonly double[] s_ms = new double[7];
        private static readonly double[] s_worst = new double[7];
        private static readonly double[] s_frame = new double[7];
        private static bool s_on;
        private static int s_frames;
        private static GameObject s_go;

        /// <summary>Split of the frame that just ended ("phys 3.1 upd 12.0 ..."), its length, and its costliest timed methods.</summary>
        public static string LastSplit = "";
        public static double LastTotal;
        public static string LastTop = "";
        public static bool On
        {
            get { return s_on; }
        }

        private static readonly string[] Short = { "fixed", "phys", "upd", "anim", "late", "render", "wait" };
        private static readonly double[] s_winMs = new double[7];
        private static int s_winFrames;

        /// <summary>Average split since the last call; starts a new window.</summary>
        public static string Window()
        {
            int n = Mathf.Max(1, s_winFrames);
            double sum = 0;
            string text = "";
            for (int i = 0; i < 7; i++)
            {
                sum += s_winMs[i];
                text += (i == 0 ? "" : " ") + Short[i] + " " + (s_winMs[i] / n).ToString("0.0", CultureInfo.InvariantCulture);
                s_winMs[i] = 0;
            }
            string line = s_winFrames + " frames, " + (sum / n).ToString("0.0", CultureInfo.InvariantCulture) + " ms avg = " + text;
            s_winFrames = 0;
            return line;
        }

        private static int s_gc;
        /// <summary>Time SessionWatch spent putting timing patches in during this frame.</summary>
        public static double PatchMs;

        private static long s_mark;
        private static int s_open = -1;
        private static int s_lastFrame = -1;

        public static void Begin()
        {
            if (s_go == null)
            {
                s_go = new GameObject("FastLoad frame phases");
                Object.DontDestroyOnLoad(s_go);
                s_go.AddComponent<PhaseFirst>();
                s_go.AddComponent<PhaseLast>();
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
            }
            for (int i = 0; i < 7; i++)
            {
                s_ms[i] = 0;
                s_worst[i] = 0;
                s_frame[i] = 0;
            }
            s_frames = 0;
            s_open = -1;
            s_lastFrame = -1;
            s_on = true;
        }

        public static string End()
        {
            s_on = false;
            int n = Mathf.Max(1, s_frames);
            string text = "";
            double sum = 0;
            for (int i = 0; i < 7; i++) sum += s_ms[i];
            for (int i = 0; i < 7; i++)
                text += (i == 0 ? "" : ", ") + Names[i] + " " + (s_ms[i] / n).ToString("0.00", CultureInfo.InvariantCulture) + " ms (worst " + s_worst[i].ToString("0", CultureInfo.InvariantCulture) + ")";
            return "frame " + (sum / n).ToString("0.00", CultureInfo.InvariantCulture) + " ms average over " + s_frames + " frames: " + text;
        }

        /// <summary>Everything from the last mark to now goes to the phase that was open; then the given phase opens.</summary>
        internal static void Mark(int opens)
        {
            if (!s_on) return;
            long now = Stopwatch.GetTimestamp();
            int frame = Time.frameCount;
            if (frame != s_lastFrame)
            {
                if (s_lastFrame >= 0)
                {
                    // Close the old frame: what was open runs up to here.
                    Add(s_open, now);
                    double total = 0;
                    for (int i = 0; i < 7; i++) total += s_frame[i];
                    LastTotal = total;
                    if (total >= 25.0)
                    {
                        string split = "";
                        for (int i = 0; i < 7; i++)
                            if (s_frame[i] >= 0.5) split += (split.Length == 0 ? "" : " ") + Short[i] + " " + s_frame[i].ToString("0.0", CultureInfo.InvariantCulture);
                        LastSplit = split;
                    }
                    else LastSplit = "";
                    LastTop = LagProfiler.FrameTop(6, total >= 25.0);
                    int gc = System.GC.CollectionCount(0);
                    if (total >= 25.0)
                    {
                        if (gc != s_gc) LastTop += (LastTop.Length == 0 ? "" : ", ") + "GC ran (" + (gc - s_gc) + ")";
                        if (PatchMs >= 1.0) LastTop += (LastTop.Length == 0 ? "" : ", ") + "FastLoad timing setup " + PatchMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms";
                    }
                    s_gc = gc;
                    PatchMs = 0;
                    LagProfiler.FrameNo++;
                    for (int i = 0; i < 7; i++) s_winMs[i] += s_frame[i];
                    s_winFrames++;
                    for (int i = 0; i < 7; i++)
                    {
                        if (s_frame[i] > s_worst[i]) s_worst[i] = s_frame[i];
                        s_frame[i] = 0;
                    }
                    s_frames++;
                }
                s_lastFrame = frame;
                s_mark = now;
                s_open = opens;
                return;
            }
            Add(s_open, now);
            s_mark = now;
            s_open = opens;
        }

        private static void Add(int phase, long now)
        {
            if (phase < 0) return;
            double ms = (now - s_mark) * 1000.0 / Stopwatch.Frequency;
            s_ms[phase] += ms;
            s_frame[phase] += ms;
        }

        private static bool s_rendering;

        private static void PreCull(Camera cam)
        {
            if (!s_on) return;
            Mark(Render);
        }

        private static void PostRender(Camera cam)
        {
            if (!s_on) return;
            // The last camera of the frame ends rendering; earlier ones are overwritten by later marks.
            Mark(Other);
        }

        internal static void FrameStart()
        {
            s_rendering = false;
        }
    }

    [DefaultExecutionOrder(-32000)]
    internal sealed class PhaseFirst : MonoBehaviour
    {
        private void FixedUpdate()
        {
            FramePhases.Mark(FramePhases.Fixed);
        }

        private void Update()
        {
            FramePhases.FrameStart();
            FramePhases.Mark(FramePhases.Update);
        }

        private void LateUpdate()
        {
            FramePhases.Mark(FramePhases.Late);
        }
    }

    [DefaultExecutionOrder(32000)]
    internal sealed class PhaseLast : MonoBehaviour
    {
        private void FixedUpdate()
        {
            FramePhases.Mark(FramePhases.Physics);
        }

        private void Update()
        {
            FramePhases.Mark(FramePhases.Anim);
        }

        private void LateUpdate()
        {
            // Up to the first camera: still counted as other until rendering starts.
            FramePhases.Mark(FramePhases.Other);
        }
    }
}
