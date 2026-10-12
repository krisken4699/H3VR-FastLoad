using System;

namespace FastLoad.Tests
{
    public static class Program
    {
        public static int Main()
        {
            SmokeTests.Run();

            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed > 0 ? 1 : 0;
        }
    }
}
