using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;

namespace FastLoad
{
    /// <summary>
    /// "[Fix] HandsAfterSceneLoad": hands that do not follow the controllers.
    ///
    /// What "not following" is, in the game's own terms: SteamVR keeps reporting where each controller is (the pose
    /// action's values change), but the hand object is only moved when SteamVR's "visual" input update runs and
    /// fires its events (SteamVR_Behaviour_Pose.UpdateTransform), and that update hangs on a per-camera callback of
    /// the SteamVR object in the scene. Seen on a big profile after the start scene loaded while mods were still
    /// loading: values moving, hands parked 0.5-0.9 m away for the rest of the session.
    ///
    /// Two repairs, each only when the fault is actually there:
    ///  1. The pose action's input set is off while a controller is connected: switch the sets that were on back on.
    ///  2. A connected controller with a valid pose whose hand object is not where the pose says, for most of a
    ///     second, outside a scene load: run SteamVR's visual input update ourselves every frame from then on
    ///     (from the eye camera's pre-cull callback when that fires, else from LateUpdate), until the next scene.
    /// When either happens the log gets one line with what was found, for tracing the cause.
    /// </summary>
    public sealed class HandGuard : MonoBehaviour
    {
        public ManualLogSource Log;

        /// <summary>A hand this far from its controller pose is not following (metres). Following measures 0.</summary>
        private const float StuckGap = 0.05f;
        private const double StuckSeconds = 0.75;

        private readonly Stopwatch m_since = Stopwatch.StartNew();
        private readonly Stopwatch m_setOff = new Stopwatch();
        private readonly Stopwatch m_stuck = new Stopwatch();
        private readonly List<SteamVR_ActionSet> m_wereOn = new List<SteamVR_ActionSet>();
        private int m_repairs;
        private bool m_drive;
        private int m_drivenFrame = -1;
        private int m_stereoCulls;
        private int m_drives;

        private void OnEnable()
        {
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, new Camera.CameraCallback(OnCameraPreCull));
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, new Camera.CameraCallback(OnCameraPreCull));
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Additive) return;
            // A new scene has its own SteamVR object and hands: judged afresh.
            if (m_drive) Timeline.Write("HND", "new scene: driving hands stopped after " + m_drives + " updates; checking again");
            m_drive = false;
            m_stuck.Reset();
        }

        private void OnCameraPreCull(Camera cam)
        {
            if (!cam.stereoEnabled) return;
            m_stereoCulls++;
            if (m_drive) Drive();
        }

        private void LateUpdate()
        {
            // The pre-cull callback did not come this frame (that may be the very fault).
            if (m_drive) Drive();
        }

        private void Drive()
        {
            if (m_drivenFrame == Time.frameCount) return;
            m_drivenFrame = Time.frameCount;
            m_drives++;
            try
            {
                SteamVR_Input.UpdateVisualActions(false);
            }
            catch (Exception e)
            {
                m_drive = false;
                Log.LogError("Hand guard: SteamVR's input update threw, driving stopped: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Update()
        {
            if (m_since.Elapsed.TotalSeconds < 0.25) return;
            m_since.Reset();
            m_since.Start();
            try
            {
                FVRMovementManager mm = GM.CurrentMovementManager;
                if (mm == null || mm.Hands == null) return;
                bool anyConnected = false;
                bool anyOff = false;
                bool anyStuck = false;
                string gaps = "";
                for (int i = 0; i < mm.Hands.Length; i++)
                {
                    FVRViveHand h = mm.Hands[i];
                    if (h == null || h.Pose == null) continue;
                    if (!h.Pose[h.HandSource].deviceIsConnected) continue;
                    anyConnected = true;
                    if (!h.Pose.active)
                    {
                        anyOff = true;
                        continue;
                    }
                    if (!h.Pose[h.HandSource].poseIsValid) continue;
                    float gap = (h.transform.localPosition - h.Pose[h.HandSource].localPosition).magnitude;
                    gaps += (gaps.Length == 0 ? "" : "/") + (int)(gap * 1000f) + "mm";
                    if (gap > StuckGap) anyStuck = true;
                }
                int culls = m_stereoCulls;
                m_stereoCulls = 0;
                if (!anyConnected || SteamVR_LoadLevel.loading)
                {
                    m_setOff.Reset();
                    m_stuck.Reset();
                    return;
                }

                // ---- 1. input set off
                if (!anyOff)
                {
                    m_setOff.Reset();
                    if (!anyStuck)
                    {
                        // Working: this is what "on" looks like.
                        SteamVR_ActionSet[] sets = SteamVR_Input.actionSets;
                        if (sets != null)
                        {
                            m_wereOn.Clear();
                            for (int i = 0; i < sets.Length; i++)
                                if (sets[i] != null && sets[i].IsActive(SteamVR_Input_Sources.Any)) m_wereOn.Add(sets[i]);
                        }
                    }
                }
                else
                {
                    if (!m_setOff.IsRunning) m_setOff.Start();
                    if (m_setOff.Elapsed.TotalSeconds >= 1.0)
                    {
                        m_setOff.Reset();
                        string names = "";
                        if (m_wereOn.Count == 0)
                        {
                            // Never seen working in this run: the game's own set.
                            SteamVR_ActionSet def = SteamVR_Input.GetActionSet("default");
                            if (def != null) m_wereOn.Add(def);
                        }
                        for (int i = 0; i < m_wereOn.Count; i++)
                        {
                            m_wereOn[i].Activate(SteamVR_Input_Sources.Any, 0, false);
                            names += (names.Length == 0 ? "" : ", ") + m_wereOn[i].GetShortName();
                        }
                        m_repairs++;
                        Say("controllers were connected but the hands' input set was off; switched on again: " + names + " (repair " + m_repairs + " this run)");
                    }
                }

                // ---- 2. hand not where its controller is
                if (!anyStuck || m_drive)
                {
                    m_stuck.Reset();
                    return;
                }
                if (!m_stuck.IsRunning) m_stuck.Start();
                if (m_stuck.Elapsed.TotalSeconds < StuckSeconds) return;
                m_stuck.Reset();
                m_drive = true;
                m_drives = 0;
                m_repairs++;
                string found = "";
                try
                {
                    SteamVR_Behaviour[] all = Resources.FindObjectsOfTypeAll(typeof(SteamVR_Behaviour)) as SteamVR_Behaviour[];
                    int on = 0;
                    for (int i = 0; all != null && i < all.Length; i++) if (all[i] != null && all[i].isActiveAndEnabled) on++;
                    Camera main = Camera.main;
                    found = "SteamVR objects " + (all != null ? all.Length : -1) + " (" + on + " switched on), eye-camera callbacks in the last 0.25 s: " + culls
                        + ", rendering paused: " + SteamVR_Render.pauseRendering + ", main camera: " + (main != null ? main.name + " stereo=" + main.stereoEnabled : "none")
                        + ", scene " + SceneManager.GetActiveScene().name;
                }
                catch (Exception e)
                {
                    found = "could not look: " + e.GetType().Name;
                }
                Say("hands were not following the controllers (hand to controller " + gaps + "); FastLoad now runs SteamVR's hand update itself until the next scene (repair "
                    + m_repairs + " this run). Found: " + found);
            }
            catch (Exception e)
            {
                enabled = false;
                Log.LogError("Hand guard switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Say(string text)
        {
            Timeline.Write("HND", text);
            Log.LogInfo("Hands: " + text);
        }
    }
}
