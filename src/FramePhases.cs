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
