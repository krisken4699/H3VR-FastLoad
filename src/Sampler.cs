using System;
using FastLoad.Early;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Runs once per frame: measures the frame that just ended on the real clock, writes slow frames and one summary
    /// line per second, keeps the bundle hooks' async list moving, and starts the report every 10 s.
    /// </summary>
    public sealed class Sampler : MonoBehaviour
    {
        public float HitchMs = 25f;

        /// <summary>
        /// A frame this long keeps the menu from counting as quiet. Twice the slow-frame line, at least 50 ms: at
        /// 72 Hz one dropped frame is 27.8 ms and happens now and then for good; with 25 ms quiet never came
        /// (first real run, 2026-10-12).
        /// </summary>
        public float QuietBreakMs
        {
            get { return Math.Max(50f, HitchMs * 2f); }
        }

        private const int ReportEveryMs = 10000;

        private double m_last = -1;
        private int m_secStart;
        private int m_secFrames;
        private double m_secWorst;
        private int m_nextReport;
        private bool m_off;
        private readonly QuietTracker m_quiet = new QuietTracker();

        private void Update()
        {
            if (m_off) return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                m_off = true;
                Timeline.Write("ERR", "sampler switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Step()
        {
            double now = Clock.NowMsExact;
            int nowMs = (int)now;
            Timeline.SetFrame(Time.frameCount);
            BundleHooks.Sweep();

            if (m_last < 0)
            {
                m_secStart = nowMs;
                m_nextReport = nowMs + ReportEveryMs;
            }
            else
            {
                double ms = now - m_last;
                bool slow = ms >= HitchMs;
                FrameLog.Add(nowMs, (float)ms);
                m_secFrames++;
                if (ms > m_secWorst) m_secWorst = ms;

                if (slow)
                {
                    Timeline.Write("HIT", "frame=" + LineFormat.N0(ms) + "ms loading=" + BundleLedger.InFlight
                        + " [" + BundleLedger.InFlightNames(4) + "] logs=[" + FrameSources.TakeAndReset() + "]");
                }
                else
                {
                    FrameSources.Reset();
                }

                int quietSince = m_quiet.Step(nowMs, Phases.At(Phase.MenuLoaded), ms >= QuietBreakMs || BundleLedger.InFlight > 0);
                if (quietSince >= 0 && Phases.Mark(Phase.Quiet, quietSince))
                {
                    Timeline.Write("PHS", "quiet since " + quietSince);
                    ReportJob.Kick();
                }
            }
            m_last = now;

            if (nowMs - m_secStart >= 1000) Second(nowMs);

            if (nowMs >= m_nextReport)
            {
                m_nextReport = nowMs + ReportEveryMs;
                ReportJob.Kick();
            }
        }

        private void Second(int nowMs)
        {
            MemSample m = new MemSample();
            m.AtMs = nowMs;
            Memory.Read(out m.Commit, out m.WorkingSet, out m.SysFree, out m.SysLimit);
            MemLog.Add(m);

            double span = nowMs - m_secStart;
            double fps = span > 0 ? m_secFrames * 1000.0 / span : 0;
            Timeline.Write("SEC", "fps=" + LineFormat.N0(fps) + " worst=" + LineFormat.N0(m_secWorst) + "ms commit=" + LineFormat.Mb(m.Commit)
                + " ws=" + LineFormat.Mb(m.WorkingSet) + " sysfree=" + LineFormat.Mb(m.SysFree)
                + " mono=" + LineFormat.Mb(GC.GetTotalMemory(false)) + " gc=" + GC.CollectionCount(0) + " bundles=" + BundleLedger.LoadedCount + " inflight=" + BundleLedger.InFlight
                + " " + HandsNow());

            BundleHooks.CheckUnloads();
            m_secStart = nowMs;
            m_secFrames = 0;
            m_secWorst = 0;
        }

        private void OnApplicationQuit()
        {
            if (!Timeline.Enabled) return;
            Timeline.Write("RUN", "quit");
            ReportJob.WriteNow();
        }

        private readonly UnityEngine.Vector3[] m_handWas = new UnityEngine.Vector3[2];
        private readonly UnityEngine.Vector3[] m_rawWas = new UnityEngine.Vector3[2];
        private bool m_saidHandPatches;

        /// <summary>Once: every mod that has a Harmony patch on the game's hand and player-rig code.</summary>
        private void SayHandPatches()
        {
            m_saidHandPatches = true;
            try
            {
                Type[] types = new Type[] { typeof(FistVR.FVRViveHand), typeof(FistVR.FVRMovementManager), typeof(FistVR.FVRPlayerBody), typeof(FistVR.FVRInteractiveObject) };
                foreach (System.Reflection.MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
                {
                    if (ReferenceEquals(m.DeclaringType, null) || Array.IndexOf(types, m.DeclaringType) < 0) continue;
                    HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    string owners = "";
                    foreach (string o in info.Owners) owners += (owners.Length == 0 ? "" : ", ") + o;
                    Timeline.Write("HND", "patched " + m.DeclaringType.Name + "." + m.Name + " by " + owners + " (prefix " + info.Prefixes.Count + ", postfix "
                        + info.Postfixes.Count + ", transpiler " + info.Transpilers.Count + ")");
                }
            }
            catch (Exception e)
            {
                Timeline.Write("HND", "could not list hand patches: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// "hands=L[init pose=ok conn=1 moved=12mm rel=310mm] R[...]": per hand, whether the game finished setting it
        /// up, whether SteamVR says its pose is valid and its device connected, how far it moved in the last second
        /// and how far it is from the head. A hand that does not follow the controller shows moved=0 with pose=ok,
        /// or pose=BAD / conn=0 when SteamVR has no controller for it.
        /// </summary>
        private string HandsNow()
        {
            try
            {
                FistVR.FVRMovementManager mm = FistVR.GM.CurrentMovementManager;
                if (mm == null || mm.Hands == null) return "hands=none";
                if (!m_saidHandPatches) SayHandPatches();
                UnityEngine.Transform head = FistVR.GM.CurrentPlayerBody != null ? FistVR.GM.CurrentPlayerBody.Head : null;
                string text = "hands=";
                for (int i = 0; i < mm.Hands.Length && i < 2; i++)
                {
                    FistVR.FVRViveHand h = mm.Hands[i];
                    if (h == null)
                    {
                        text += (i == 0 ? "L" : " R") + "[null]";
                        continue;
                    }
                    UnityEngine.Vector3 at = h.transform.position;
                    int moved = (int)((at - m_handWas[i]).magnitude * 1000f);
                    m_handWas[i] = at;
                    string pose = "?";
                    string conn = "?";
                    string raw = "?";
                    try
                    {
                        if (h.Pose != null)
                        {
                            pose = (h.Pose[h.HandSource].poseIsValid ? "ok" : "BAD") + " input=" + (h.Pose.active ? "on" : "OFF");
                            conn = h.Pose[h.HandSource].deviceIsConnected ? "1" : "0";
                            // What SteamVR itself reports for the controller, before the game does anything with it.
                            UnityEngine.Vector3 rawAt = h.Pose[h.HandSource].localPosition;
                            raw = ((int)((rawAt - m_rawWas[i]).magnitude * 1000f)) + "mm";
                            m_rawWas[i] = rawAt;
                            // How far the hand object sits from where SteamVR says the controller is (same space):
                            // small and steady when the hand follows, anything when it is stuck somewhere.
                            raw += " gap=" + (int)((h.transform.localPosition - rawAt).magnitude * 1000f) + "mm";
                        }
                    }
                    catch (Exception)
                    {
                        pose = "err";
                    }
                    text += (i == 0 ? "L" : " R") + "[" + (h.HasInit ? "init" : "NOINIT") + " " + h.CMode + " pose=" + pose + " conn=" + conn + " steamvrMoved=" + raw + " handMoved=" + moved + "mm"
                        + (head != null ? " rel=" + (int)((at - head.position).magnitude * 1000f) + "mm" : "") + (h.gameObject.activeInHierarchy ? "" : " OFF") + "]";
                }
                return text;
            }
            catch (Exception e)
            {
                return "hands=err:" + e.GetType().Name;
            }
        }
    }
}
