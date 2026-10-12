namespace FastLoad.Early
{
    /// <summary>What the plugin and the patcher share for one run.</summary>
    public static class Timeline
    {
        /// <summary>False when the probe is switched off or could not open its file: nothing else may run then.</summary>
        public static bool Enabled;
        public static string Dir = "";
        public static string Stamp = "";
        public static string Profile = "";
        public static string Args = "";
        /// <summary>Cost of the restart on launch in ms, as measured by the mod that did it; -1 = no restart.</summary>
        public static int RestartCostMs = -1;
        public static bool NativeHooks;
        public static TimelineWriter Writer;

        public static void Write(string kind, string text)
        {
            TimelineWriter w = Writer;
            if (w != null) w.Write(kind, text);
        }

        public static void SetFrame(int frame)
        {
            TimelineWriter w = Writer;
            if (w != null) w.Frame = frame;
        }
    }
}
