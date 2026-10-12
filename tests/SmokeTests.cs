namespace FastLoad.Tests
{
    public static class SmokeTests
    {
        public static void Run()
        {
            T.Group("Smoke");
            T.Eq(4, 2 + 2, "harness runs");
        }
    }
}
