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
            if (m_drive) Timeline.Write("HND", "new scene: driving hands stopped after " + m_drives + " updates (stage " + m_stage + "); checking again");
            m_drive = false;
            m_stage = 0;
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

        /// <summary>
        /// 0 = SteamVR's own update is run for it; 1 = the hand's pose component was switched off and on again;
        /// 2 = the hand object is put where the controller is, by hand, every frame.
        /// </summary>
        private int m_stage;
        private readonly Stopwatch m_sinceStage = new Stopwatch();

        private void Drive()
        {
            if (m_drivenFrame == Time.frameCount && m_stage < 2) return;
            bool first = m_drivenFrame != Time.frameCount;
            m_drivenFrame = Time.frameCount;
            if (first) m_drives++;
            try
            {
                if (first) SteamVR_Input.UpdateVisualActions(false);
                if (m_stage < 2) return;
                // Last resort, and done at every chance in the frame so that it is the last word before the picture.
                FVRMovementManager mm = GM.CurrentMovementManager;
                if (mm == null || mm.Hands == null) return;
                for (int i = 0; i < mm.Hands.Length; i++)
                {
                    FVRViveHand h = mm.Hands[i];
                    if (h == null || h.Pose == null || !h.Pose[h.HandSource].deviceIsConnected || !h.Pose[h.HandSource].poseIsValid) continue;
                    // Same space as the pose: a hand that follows by itself has exactly these values.
                    h.transform.localPosition = h.Pose[h.HandSource].localPosition;
                    h.transform.localRotation = h.Pose[h.HandSource].localRotation;
                }
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
            if (Paused)
            {
                m_drive = false;
                return;
            }
            try
            {
                FVRMovementManager mm = GM.CurrentMovementManager;
                if (mm == null || mm.Hands == null) return;
                bool anyConnected = false;
                bool anyOff = false;
                bool anyStuck = false;
                string gaps = "";
                // ---- 0. the cause found on 2026-10-13: the component that moves the hand listens to the wrong action.
                // The hand itself (FVRViveHand.Pose) has /actions/default/in/Pose; its SteamVR_Behaviour_Pose came up
                // bound to /actions/mixedreality/in/ExternalCamera, an action of a set that is never on, so it never
                // moved the hand. Put right at once, connected controller or not, so it is never seen.
                for (int i = 0; i < mm.Hands.Length; i++)
                {
                    FVRViveHand h = mm.Hands[i];
                    if (h == null || h.Pose == null) continue;
                    SteamVR_Behaviour_Pose bp = h.GetComponent<SteamVR_Behaviour_Pose>();
                    if (bp == null || bp.poseAction == null) continue;
                    string has = bp.poseAction.fullPath;
                    string wants = h.Pose.fullPath;
                    if (string.IsNullOrEmpty(wants) || string.Equals(has, wants, StringComparison.OrdinalIgnoreCase)) continue;
                    // Off and on around the change: that is where the component lets go of one action and takes hold of the other.
                    bp.enabled = false;
                    bp.poseAction = h.Pose;
                    bp.enabled = true;
                    m_repairs++;
                    Say("hand '" + h.name + "' was listening to " + has + " instead of " + wants + " and could not move; set right (repair " + m_repairs + " this run, scene "
                        + SceneManager.GetActiveScene().name + ")");
                }
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
                if (m_drive)
                {
                    m_stuck.Reset();
                    if (!anyStuck || m_stage >= 2 || m_sinceStage.Elapsed.TotalSeconds < 0.75) return;
                    // Still not following after the last step: the next one.
                    m_stage++;
                    m_sinceStage.Reset();
                    m_sinceStage.Start();
                    if (m_stage == 1)
                    {
                        int toggled = 0;
                        for (int i = 0; i < mm.Hands.Length; i++)
                        {
                            SteamVR_Behaviour_Pose bp = mm.Hands[i] != null ? mm.Hands[i].GetComponent<SteamVR_Behaviour_Pose>() : null;
                            if (bp == null) continue;
                            bp.enabled = false;
                            bp.enabled = true;
                            toggled++;
                        }
                        Say("still not following (" + gaps + "); switched the pose component of " + toggled + " hand(s) off and on");
                    }
                    else Say("still not following (" + gaps + "); FastLoad now puts the hands where the controllers are itself, every frame");
                    return;
                }
                if (!anyStuck)
                {
                    m_stuck.Reset();
                    return;
                }
                if (!m_stuck.IsRunning) m_stuck.Start();
                if (m_stuck.Elapsed.TotalSeconds < StuckSeconds) return;
                m_stuck.Reset();
                m_drive = true;
                m_stage = 0;
                m_sinceStage.Reset();
                m_sinceStage.Start();
                m_drives = 0;
                m_repairs++;
                for (int i = 0; i < mm.Hands.Length; i++) Timeline.Write("HND", Describe(mm.Hands[i]));
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

        /// <summary>What a hand object is made of and hangs on, for finding out why it does not move.</summary>
        /// <summary>Test switch: the guard watches and reports but repairs nothing.</summary>
        public static bool Paused;

        internal static string Describe(FVRViveHand h)
        {
            try
            {
                if (h == null) return "hand: null";
                string text = "hand '" + h.name + "' source=" + h.HandSource + " parent='" + (h.transform.parent != null ? h.transform.parent.name : "none") + "' localPos="
                    + h.transform.localPosition.ToString("F3") + " pose=" + h.Pose[h.HandSource].localPosition.ToString("F3") + " device=" + h.Pose[h.HandSource].trackedDeviceIndex;
                SteamVR_Behaviour_Pose bp = h.GetComponent<SteamVR_Behaviour_Pose>();
                if (bp == null) text += " | NO SteamVR_Behaviour_Pose on it";
                else text += " | pose component: enabled=" + bp.enabled + " source=" + bp.inputSource + " action=" + (bp.poseAction != null ? bp.poseAction.GetShortName() : "none")
                    + " actionActive=" + (bp.poseAction != null && bp.poseAction.active) + " origin='" + (bp.origin != null ? bp.origin.name : "none") + "' index=" + bp.GetDeviceIndex();
                try
                {
                    SteamVR_Action_Pose hp = h.Pose;
                    text += " | hand's action " + hp.fullPath + " set=" + hp.actionSet.GetShortName() + " any[active=" + hp.active + " binding=" + hp[SteamVR_Input_Sources.Any].activeBinding
                        + " setOn=" + hp.actionSet.IsActive(SteamVR_Input_Sources.Any) + "] own[active=" + hp[h.HandSource].active + " binding=" + hp[h.HandSource].activeBinding
                        + " setOn=" + hp.actionSet.IsActive(h.HandSource) + " device=" + hp[h.HandSource].trackedDeviceIndex + "] anyDevice=" + hp[SteamVR_Input_Sources.Any].trackedDeviceIndex;
                    if (bp != null && bp.poseAction != null)
                    {
                        SteamVR_Action_Pose pa = bp.poseAction;
                        text += " | component's action " + pa.fullPath + " set=" + pa.actionSet.GetShortName() + " any[active=" + pa.active + " binding=" + pa[SteamVR_Input_Sources.Any].activeBinding
                            + " setOn=" + pa.actionSet.IsActive(SteamVR_Input_Sources.Any) + "] own[active=" + pa[bp.inputSource].active + " binding=" + pa[bp.inputSource].activeBinding + "]"
                            + " sameObject=" + ReferenceEquals(pa, hp);
                    }
                    SteamVR_ActionSet[] sets = SteamVR_Input.actionSets;
                    text += " | sets:";
                    for (int i = 0; sets != null && i < sets.Length; i++)
                        if (sets[i] != null) text += " " + sets[i].GetShortName() + "(any=" + sets[i].IsActive(SteamVR_Input_Sources.Any) + ",L=" + sets[i].IsActive(SteamVR_Input_Sources.LeftHand)
                            + ",R=" + sets[i].IsActive(SteamVR_Input_Sources.RightHand) + ")";
                }
                catch (Exception e)
                {
                    text += " | actions: " + e.GetType().Name + ": " + e.Message;
                }
                text += " | components:";
                Component[] all = h.GetComponents<Component>();
                for (int i = 0; i < all.Length; i++) if (all[i] != null) text += " " + all[i].GetType().Name + ((all[i] is Behaviour) && !((Behaviour)all[i]).enabled ? "(off)" : "");
                // Up the tree: a switched-off or oddly placed parent explains a lot.
                text += " | path:";
                for (Transform t = h.transform; t != null; t = t.parent)
                {
                    text += " <- " + t.name + (t.gameObject.activeSelf ? "" : "(OFF)");
                    SteamVR_Behaviour_Pose up = t != h.transform ? t.GetComponent<SteamVR_Behaviour_Pose>() : null;
                    if (up != null) text += "[pose component here: enabled=" + up.enabled + " source=" + up.inputSource + " localPos=" + t.localPosition.ToString("F3") + "]";
                }
                Rigidbody rb = h.GetComponent<Rigidbody>();
                if (rb != null) text += " | rigidbody kinematic=" + rb.isKinematic + " constraints=" + rb.constraints;
                return text;
            }
            catch (Exception e)
            {
                return "hand: could not describe: " + e.GetType().Name + ": " + e.Message;
            }
        }

        private void Say(string text)
        {
            Timeline.Write("HND", text);
            Log.LogInfo("Hands: " + text);
        }
    }
}
