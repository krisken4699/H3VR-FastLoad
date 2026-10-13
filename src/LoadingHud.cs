using System;
using BepInEx.Logging;
using FastLoad.Early;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;

namespace FastLoad
{
    /// <summary>
    /// "[Hud] LoadingWindow": one line of text in front of the player while something loads ("Loading X from Y  5/24"),
    /// gone when it is done.
    ///
    /// It hangs on the head camera's own transform, so it moves with the head pose the frame is drawn with and
    /// cannot lag behind or stutter. Text and backing are drawn with depth testing off and last in the frame, so a
    /// gun held in front of it does not hide it. The backing is as wide as the text.
    ///
    /// Also here, because it reads the same feed: while a load someone is waiting for is in flight (the part
    /// bundles a modular gun cannot appear without), Unity's background loading is given more of each frame.
    /// </summary>
    public sealed class LoadingHud : MonoBehaviour
    {
        public ManualLogSource Log;
        public bool ShowWindow = true;
        public bool Boost = true;
        /// <summary>Also show background loading (the item lists after game start).</summary>
        public bool Everything;
        /// <summary>Measured on one ModulAR15 (48 bundles): BelowNormal 65 s, Normal 57 s at 26-45 fps, High 48 s at 14-46 fps.</summary>
        public ThreadPriority BoostTo = ThreadPriority.Normal;

        /// <summary>Metres in front of the eyes, and above the line of sight.</summary>
        private const float Distance = 0.75f;
        private const float Up = 0.2f;
        /// <summary>Height of a capital letter, metres.</summary>
        private const float Scale = 0.0022f;
        private const int FontSize = 64;
        /// <summary>A load shorter than this shows nothing.</summary>
        private const int MinAgeMs = 400;

        private GameObject m_root;
        private TextMesh m_text;
        private Transform m_back;
        private Font m_font;
        private string m_shown = "";
        private bool m_visible;
        private bool m_boosted;
        private readonly System.Diagnostics.Stopwatch m_sinceUrgent = new System.Diagnostics.Stopwatch();
        private ThreadPriority m_before;
        private bool m_broken;
        private const double HoldSeconds = 0.7;
        private readonly System.Diagnostics.Stopwatch m_sinceShown = new System.Diagnostics.Stopwatch();

        private void LateUpdate()
        {
            try
            {
                if (Boost) BoostStep();
                if (MwoBridge.PartLevel && MwoBridge.Mode == "on") PartsTick();
                if (!ShowWindow || m_broken) return;
                string text;
                bool show = Everything ? LoadFeed.Line(Clock.NowMs, MinAgeMs, out text) : LoadFeed.WaitedLine(Clock.NowMs, out text);
                if (show)
                {
                    m_sinceShown.Reset();
                    m_sinceShown.Start();
                }
                else if (m_visible && m_sinceShown.Elapsed.TotalSeconds < HoldSeconds)
                {
                    // Between two loads of a batch: the last line stays instead of blinking.
                    return;
                }
                if (!show)
                {
                    if (m_visible && m_root != null) m_root.SetActive(false);
                    if (m_visible) Timeline.Write("HUD", "(hidden)");
                    m_visible = false;
                    m_shown = "";
                    return;
                }
                Transform head = Head();
                if (head == null) return;
                if (m_root == null) Build();
                if (m_root.transform.parent != head)
                {
                    m_root.transform.SetParent(head, false);
                    m_root.transform.localPosition = new Vector3(0f, Up, Distance);
                    m_root.transform.localRotation = Quaternion.identity;
                    // The head can be scaled; the window is not.
                    Vector3 s = head.lossyScale;
                    m_root.transform.localScale = new Vector3(1f / Mathf.Max(0.01f, s.x), 1f / Mathf.Max(0.01f, s.y), 1f / Mathf.Max(0.01f, s.z));
                }
                if (!m_visible) m_root.SetActive(true);
                m_visible = true;
                if (text != m_shown) SetText(text);
            }
            catch (Exception e)
            {
                m_broken = true;
                Log.LogError("Loading window switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// The game leaves background loading at its slowest after every scene change (2 ms a frame), which is right
        /// for things nobody waits for. A gun that cannot appear until 50 part bundles are in is waited for.
        /// </summary>
        private void BoostStep()
        {
            // Bundles of one spawn come one after another with a frame or two between them: hold on through the gaps.
            if (LoadFeed.UrgentActive > 0)
            {
                m_sinceUrgent.Reset();
                m_sinceUrgent.Start();
            }
            bool want = m_sinceUrgent.IsRunning && m_sinceUrgent.Elapsed.TotalSeconds < 1.5;
            if (want && !m_boosted)
            {
                m_before = Application.backgroundLoadingPriority;
                if (m_before == ThreadPriority.Low || m_before == ThreadPriority.BelowNormal)
                {
                    Application.backgroundLoadingPriority = BoostTo;
                    m_boosted = true;
                    Timeline.Write("DEF", "background loading " + m_before + " -> " + BoostTo + " while a spawn waits for its bundles");
                }
            }
            else if (!want && m_boosted)
            {
                m_boosted = false;
                // Only put back what is still ours: a scene change in between sets its own.
                if (Application.backgroundLoadingPriority == BoostTo) Application.backgroundLoadingPriority = m_before;
                Timeline.Write("DEF", "background loading back to " + Application.backgroundLoadingPriority);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PartsTick()
        {
            MwoParts.Tick();
        }

        private static Transform Head()
        {
            Camera cam = Camera.main;
            if (cam != null) return cam.transform;
            return GM.CurrentPlayerBody != null ? GM.CurrentPlayerBody.Head : null;
        }

        private void Build()
        {
            m_font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            m_root = new GameObject("FastLoad loading window");

            GameObject textGo = new GameObject("text");
            textGo.transform.SetParent(m_root.transform, false);
            textGo.transform.localScale = new Vector3(Scale, Scale, Scale);
            m_text = textGo.AddComponent<TextMesh>();
            m_text.font = m_font;
            m_text.fontSize = FontSize;
            m_text.characterSize = 1f;
            m_text.anchor = TextAnchor.MiddleCenter;
            m_text.alignment = TextAlignment.Center;
            m_text.color = new Color(1f, 1f, 1f, 1f);
            MeshRenderer tr = textGo.GetComponent<MeshRenderer>();
            // The font's own shader already ignores depth; last in the frame so nothing is drawn over it.
            Material textMat = new Material(m_font.material);
            textMat.renderQueue = 4001;
            tr.sharedMaterial = textMat;
            tr.shadowCastingMode = ShadowCastingMode.Off;
            tr.receiveShadows = false;

            GameObject backGo = new GameObject("back");
            backGo.transform.SetParent(m_root.transform, false);
            backGo.transform.localPosition = new Vector3(0f, 0f, 0.001f);
            Mesh quad = new Mesh();
            quad.vertices = new Vector3[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            quad.RecalculateBounds();
            backGo.AddComponent<MeshFilter>().sharedMesh = quad;
            MeshRenderer br = backGo.AddComponent<MeshRenderer>();
            Material backMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            backMat.SetInt("_ZTest", (int)CompareFunction.Always);
            backMat.SetInt("_ZWrite", 0);
            backMat.SetInt("_Cull", (int)CullMode.Off);
            backMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            backMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            backMat.SetColor("_Color", new Color(0f, 0f, 0f, 0.72f));
            backMat.renderQueue = 4000;
            br.sharedMaterial = backMat;
            br.shadowCastingMode = ShadowCastingMode.Off;
            br.receiveShadows = false;
            m_back = backGo.transform;
            m_shown = "";
        }

        private void SetText(string text)
        {
            m_shown = text;
            m_text.text = text;
            Timeline.Write("HUD", text);
            // Width from the font itself: the renderer's bounds are a world-space box and grow when the head turns.
            m_font.RequestCharactersInTexture(text, FontSize, FontStyle.Normal);
            float px = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                CharacterInfo info;
                if (m_font.GetCharacterInfo(text[i], out info, FontSize, FontStyle.Normal)) px += info.advance;
            }
            // TextMesh draws a font pixel as characterSize / 10 units.
            float width = px * 0.1f * Scale;
            float height = FontSize * 0.1f * Scale * 1.25f;
            m_back.localScale = new Vector3(width + height * 0.9f, height * 1.5f, 1f);
        }
    }
}
