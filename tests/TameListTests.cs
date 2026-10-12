using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class TameListTests
    {
        public static void Run()
        {
            T.Group("TameList");
            T.Eq(0, TameList.Parse(null).Count, "null = nothing");
            T.Eq(0, TameList.Parse("  ").Count, "blank = nothing");

            List<KeyValuePair<string, double>> two = TameList.Parse(" JerryTF.ThngFlingerPGin.SearchTarget@0.5 ; Some.Type.Method @ 2");
            T.Eq(2, two.Count, "two entries, comma or semicolon");
            T.Eq("JerryTF.ThngFlingerPGin.SearchTarget", two[0].Key, "method name");
            T.True(two[0].Value == 0.5, "seconds, with a point whatever the PC's language");
            T.Eq("Some.Type.Method", two[1].Key, "spaces around the @ do not matter");
            T.True(two[1].Value == 2.0, "whole seconds");

            T.Eq(0, TameList.Parse("NoSeconds.Method").Count, "no @ = skipped");
            T.Eq(0, TameList.Parse("A.B@").Count, "no number = skipped");
            T.Eq(0, TameList.Parse("A.B@x").Count, "not a number = skipped");
            T.Eq(0, TameList.Parse("A.B@0").Count, "zero = skipped");
            T.Eq(0, TameList.Parse("A.B@-1").Count, "negative = skipped");
            T.Eq(0, TameList.Parse("JustAMethod@1").Count, "no type = skipped");
            T.Eq(1, TameList.Parse("A.B@1, A.B@3").Count, "the same method twice counts once");
        }
    }
}
