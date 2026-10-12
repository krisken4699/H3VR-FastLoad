using System;
using Valve.VR;

namespace FastLoad
{
    /// <summary>
    /// Black in the headset in place of something nobody is meant to look at: the main menu on the way to the
    /// [Start] Scene, or a game that stands still while a held plugin starts. The same two calls the game's own
    /// scene loader makes around a load.
    /// </summary>
    public static class Curtain
    {
        public static bool Closed;

        public static void Close()
        {
            try
            {
                CVRCompositor compositor = OpenVR.Compositor;
                if (compositor != null)
                {
                    compositor.FadeToColor(0f, 0f, 0f, 0f, 1f, false);
                    compositor.FadeGrid(0f, true);
                }
                SteamVR_Render.pauseRendering = true;
                Closed = true;
            }
            catch (Exception)
            {
                // No headset interface: nothing to hide from.
            }
        }

        public static void Open(float seconds)
        {
            Closed = false;
            try
            {
                SteamVR_Render.pauseRendering = false;
                CVRCompositor compositor = OpenVR.Compositor;
                if (compositor != null)
                {
                    compositor.FadeGrid(0f, false);
                    compositor.FadeToColor(seconds, 0f, 0f, 0f, 0f, false);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
