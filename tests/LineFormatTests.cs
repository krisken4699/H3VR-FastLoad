using System.Globalization;
using System.Threading;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class LineFormatTests
    {
        public static void Run()
        {
            T.Group("LineFormat");

            T.Eq("   48213 f=1432 LOG  hello", LineFormat.Line(48213, 1432, "LOG", "hello"), "line with frame");
            T.Eq("      12 f=- RUN  x", LineFormat.Line(12, -1, "RUN", "x"), "no frame yet prints -");

            // FlattenKeepsOneLine
            string flat = LineFormat.Flatten("first\r\n  at A.B ()\n  at C.D ()\rend");
            T.True(flat.IndexOf('\n') < 0 && flat.IndexOf('\r') < 0, "flatten removes every line break");
            T.True(flat.Contains("first") && flat.Contains("at A.B ()") && flat.Contains("at C.D ()") && flat.Contains("end"), "flatten keeps the text");
            T.Eq("", LineFormat.Flatten(null), "flatten null is empty");

            T.Eq("412MB", LineFormat.Mb(412L * 1048576L + 5), "Mb");
            T.Eq("+388MB", LineFormat.SignedMb(388L * 1048576L), "SignedMb plus");
            T.Eq("-12MB", LineFormat.SignedMb(-12L * 1048576L), "SignedMb minus");
            T.Eq("+0MB", LineFormat.SignedMb(0), "SignedMb zero");
            T.Eq("12.3 s", LineFormat.Sec(12345), "Sec");
            T.Eq("not seen", LineFormat.Sec(-1), "Sec of a mark that never came");

            // NumbersIgnoreLocale
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                T.Eq("187", LineFormat.N0(186.6), "N0 rounds");
                T.Eq("18.5", LineFormat.N1(18.46), "N1 uses a dot in a comma locale");
                T.Eq("12.3 s", LineFormat.Sec(12345), "Sec uses a dot in a comma locale");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }
    }
}
