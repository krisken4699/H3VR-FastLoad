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

                int quietSince = m_quiet.Step(nowMs, Phases.At(Phase.MenuLoaded), slow || BundleLedger.InFlight > 0);
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
                + " mono=" + LineFormat.Mb(GC.GetTotalMemory(false)) + " bundles=" + BundleLedger.LoadedCount + " inflight=" + BundleLedger.InFlight);

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
    }
}
