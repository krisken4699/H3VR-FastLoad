using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class CfgPeekTests
    {
        public static void Run()
        {
            T.Group("CfgPeek");
            string cfg =
                "## Settings file was created by plugin FastLoad v0.1.0\r\n" +
                "## Plugin GUID: invent60.h3vr.fastload\r\n\r\n" +
                "[Other]\r\n\r\nEnabled = true\r\n\r\n" +
                "[Probe]\r\n\r\n" +
                "## Turns the whole probe off.\r\n# Setting type: Boolean\r\n# Default value: true\r\n" +
                "Enabled = false\r\n\r\n" +
                "HitchMs = 40\r\nKeepRuns = banana\r\n";

            T.Eq("false", CfgPeek.Value(cfg, "Probe", "Enabled"), "value from the right section");
            T.Eq(false, CfgPeek.Bool(cfg, "Probe", "Enabled", true), "bool read");
            T.Eq(true, CfgPeek.Bool(cfg, "Other", "Enabled", false), "same key, other section");
            T.Eq(40, CfgPeek.Int(cfg, "Probe", "HitchMs", 25), "int read");
            T.Eq(6, CfgPeek.Int(cfg, "Probe", "KeepRuns", 6), "garbled int falls back");
            T.Eq(true, CfgPeek.Bool(cfg, "Probe", "NativeHooks", true), "missing key falls back");
            T.Eq(true, CfgPeek.Bool("", "Probe", "Enabled", true), "empty file falls back");
            T.Eq(true, CfgPeek.Bool(null, "Probe", "Enabled", true), "no file falls back");
            T.Eq((string)null, CfgPeek.Value(cfg, "Nope", "Enabled"), "missing section is null");
        }
    }
}
