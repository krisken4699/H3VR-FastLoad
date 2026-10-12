using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class LaterListTests
    {
        public static void Run()
        {
            T.Group("LaterList");

            T.Eq(0, LaterList.Parse(null).Count, "null = nothing held back");
            T.Eq(0, LaterList.Parse("").Count, "empty = nothing held back");
            T.Eq(0, LaterList.Parse("  , ;  ").Count, "only separators = nothing held back");

            List<string> two = LaterList.Parse(" Sosig Hat Realism ,NotGodlike.h3vr.customsosigmodelframework ");
            T.Eq(2, two.Count, "two entries");
            T.Eq("Sosig Hat Realism", two[0], "spaces inside a name stay, spaces around it go");
            T.Eq("NotGodlike.h3vr.customsosigmodelframework", two[1], "a GUID entry");

            T.Eq(2, LaterList.Parse("a;b").Count, "semicolons separate too");
            T.Eq(1, LaterList.Parse("Abc, abc ,ABC").Count, "the same entry twice counts once");

            T.True(LaterList.Has(two, "some.guid", "sosig hat realism"), "found by name, any capitals");
            T.True(LaterList.Has(two, "notgodlike.h3vr.CustomSosigModelFramework", "CustomSosigModelFramework"), "found by GUID, any capitals");
            T.True(!LaterList.Has(two, "some.guid", "Sosig Hat"), "part of a name is not a match");
            T.True(!LaterList.Has(two, null, null), "nothing to compare");
            T.True(!LaterList.Has(null, "a", "b"), "no list");
            T.True(!LaterList.Has(new List<string>(), "a", "b"), "empty list");
        }
    }
}
