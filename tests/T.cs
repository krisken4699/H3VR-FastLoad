using System;
using System.Collections.Generic;

namespace FastLoad.Tests
{
    /// <summary>Tiny assert helper: counts, prints failures, never throws.</summary>
    public static class T
    {
        public static int Passed;
        public static int Failed;
        private static string s_group = "";

        public static void Group(string name)
        {
            s_group = name;
        }

        public static void Eq<X>(X expected, X actual, string what)
        {
            if (EqualityComparer<X>.Default.Equals(expected, actual))
            {
                Passed++;
                return;
            }
            Failed++;
            Console.WriteLine("FAIL " + s_group + ": " + what + "\n     expected: " + expected + "\n     actual:   " + actual);
        }

        public static void True(bool condition, string what)
        {
            if (condition)
            {
                Passed++;
                return;
            }
            Failed++;
            Console.WriteLine("FAIL " + s_group + ": " + what);
        }
    }
}
