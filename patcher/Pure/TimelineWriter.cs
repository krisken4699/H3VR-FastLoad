using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>
    /// Appends timeline lines to one file. Safe from any thread. Every line is flushed to the operating system as it
    /// is written, so a game crash loses nothing that was already written.
    /// </summary>
    public sealed class TimelineWriter
    {
        private readonly object m_gate = new object();
        private StreamWriter m_out;

        /// <summary>Unity frame number, set by the sampler each frame; -1 until the first frame.</summary>
        public volatile int Frame = -1;

        public bool Open(string path, out string error)
        {
            error = "";
            try
            {
                FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false));
                w.AutoFlush = true;
                m_out = w;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        public void Write(string kind, string text)
        {
            if (m_out == null) return;
            string line = LineFormat.Line(Clock.NowMs, Frame, kind, LineFormat.Flatten(text));
            Monitor.Enter(m_gate);
            try
            {
                if (m_out != null) m_out.WriteLine(line);
            }
            catch (Exception)
            {
                // A full disk or a closed handle must never reach the caller (it may be another mod's log call).
            }
            finally
            {
                Monitor.Exit(m_gate);
            }
        }

        public void Close()
        {
            Monitor.Enter(m_gate);
            try
            {
                if (m_out != null) m_out.Close();
            }
            catch (Exception)
            {
            }
            finally
            {
                m_out = null;
                Monitor.Exit(m_gate);
            }
        }
    }
}
