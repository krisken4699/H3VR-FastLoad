using System.Runtime.CompilerServices;

namespace FastLoad.Early
{
    /// <summary>
    /// The only door from preloader code to BundleHooks. BundleHooks has fields of Unity types; a method that calls
    /// it directly would make the runtime look at those types when that method is compiled, which for the log mirror
    /// is during the preloader. This method is compiled only when it is first called: at "Chainloader started".
    /// </summary>
    public static class HookGate
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Go()
        {
            BundleHooks.Install(Timeline.NativeHooks);
        }
    }
}
