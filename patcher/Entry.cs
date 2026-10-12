using System.Collections.Generic;
using Mono.Cecil;

namespace FastLoad.Early
{
    /// <summary>BepInEx preloader patcher. Patches no assembly; it only starts the probe as early as possible.</summary>
    public static class Entry
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        public static void Initialize()
        {
        }
    }
}
