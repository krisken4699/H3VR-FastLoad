using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using FastLoad.Early;
using HarmonyLib;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// "[Tame]": mod code that costs time every frame for nothing, found with the lag profiler (tools/tests/07).
    ///
    /// 1. ConfigRebinders. Some plugins bind their whole config again in Update or FixedUpdate, every tick
    ///    (measured: 0.95 ms a physics tick for one plugin with 8 settings, two such plugins on the test profile).
    ///    Binding a setting that is bound already just hands back the same entry, so every call after the first is
    ///    wasted. Such methods are recognised by their code, on any profile: a plugin's per-frame method (and what
    ///    it calls in its own class) that does nothing but Config.Bind and store the results, with no branches.
    ///    They run once and are skipped from then on.
    ///
    /// 2. Throttle. A list of "Namespace.Type.Method@seconds": that method runs at most once per that many
    ///    seconds, other calls are skipped. For per-frame searches that do not need to be per-frame. The list
    ///    lives in the config so that new offenders need no new FastLoad build.
    /// </summary>
    public sealed class Tame : MonoBehaviour
    {
        public ManualLogSource Log;
        public bool Rebinders = true;
        public string ThrottleList = "";

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly string[] FrameMethods = new string[] { "Update", "FixedUpdate", "LateUpdate" };

        private sealed class Gate
        {
            public long EveryTicks;
            public long Last;
            /// <summary>Calls let through before the gate closes for good (config rebinders); -1 = by time.</summary>
            public int Runs = -1;
        }

        private static readonly Dictionary<MethodBase, Gate> s_gates = new Dictionary<MethodBase, Gate>();
        private readonly Dictionary<Type, bool> m_looked = new Dictionary<Type, bool>();
        private readonly Dictionary<string, bool> m_throttled = new Dictionary<string, bool>();
        private readonly Stopwatch m_since = Stopwatch.StartNew();
        private Harmony m_harmony;
        private int m_passes;
        private double m_nextPass = 1.0;

        private void Update()
        {
            // Twice: once when everything BepInEx started is there, once more for plugins that were held back.
            if (m_passes >= 2 || m_since.Elapsed.TotalSeconds < m_nextPass) return;
            m_passes++;
            m_nextPass = 60.0;
            try
            {
                if (m_harmony == null) m_harmony = new Harmony("ken.h3vr.fastload.tame");
                if (Rebinders) FindRebinders();
                ApplyThrottles();
            }
            catch (Exception e)
            {
                Log.LogError("Tame: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- gate shared by both

        private static bool GatePre(MethodBase __originalMethod)
        {
            Gate g;
            if (!s_gates.TryGetValue(__originalMethod, out g)) return true;
            if (g.Runs >= 0)
            {
                if (g.Runs == 0) return false;
                g.Runs--;
                return true;
            }
            long now = Stopwatch.GetTimestamp();
            if (g.Last != 0 && now - g.Last < g.EveryTicks) return false;
            g.Last = now;
            return true;
        }

        private bool Close(MethodInfo method, Gate gate)
        {
            if (s_gates.ContainsKey(method)) return false;
            s_gates[method] = gate;
            m_harmony.Patch(method, new HarmonyMethod(typeof(Tame).GetMethod("GatePre", Any)));
            return true;
        }

        // ---- 1. config rebinders

        private void FindRebinders()
        {
            foreach (KeyValuePair<string, PluginInfo> pair in Chainloader.PluginInfos)
            {
                BaseUnityPlugin instance = pair.Value != null ? pair.Value.Instance : null;
                if (ReferenceEquals(instance, null) || instance == null) continue;
                Type type = instance.GetType();
                if (m_looked.ContainsKey(type) || ReferenceEquals(type, typeof(Plugin))) continue;
                m_looked[type] = true;
                for (int i = 0; i < FrameMethods.Length; i++)
                {
                    MethodInfo m = null;
                    try
                    {
                        m = type.GetMethod(FrameMethods[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    }
                    catch (Exception)
                    {
                    }
                    if (ReferenceEquals(m, null)) continue;
                    int binds = 0;
                    bool only;
                    try
                    {
                        only = OnlyBinds(m, type, 0, ref binds);
                    }
                    catch (Exception)
                    {
                        only = false;
                    }
                    if (!only || binds == 0) continue;
                    Gate g = new Gate();
                    // Once more, in case its Awake did not bind them.
                    g.Runs = 1;
                    if (!Close(m, g)) continue;
                    string text = pair.Value.Metadata.Name + " (" + type.FullName + "." + m.Name + ") bound its " + binds + " settings again every tick; now it runs once";
                    Timeline.Write("DEF", "tame: " + text);
                    Log.LogInfo("Tame: " + text);
                }
            }
        }

        private static Dictionary<short, OpCode> s_ops;

        private static void LoadOps()
        {
            s_ops = new Dictionary<short, OpCode>();
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!ReferenceEquals(fields[i].FieldType, typeof(OpCode))) continue;
                OpCode op = (OpCode)fields[i].GetValue(null);
                s_ops[op.Value] = op;
            }
        }

        /// <summary>
        /// True when the method has no branches and every call in it is Config.Bind, the plugin's Config getter, a
        /// BepInEx.Configuration constructor, or a method of the same class for which the same holds.
        /// </summary>
        private static bool OnlyBinds(MethodBase method, Type owner, int depth, ref int binds)
        {
            if (depth > 3) return false;
            if (s_ops == null) LoadOps();
            MethodBody body = method.GetMethodBody();
            if (body == null) return false;
            byte[] il = body.GetILAsByteArray();
            if (il == null || il.Length > 4000) return false;
            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int p = 0;
            while (p < il.Length)
            {
                short code = il[p++];
                if (code == 0xFE && p < il.Length) code = unchecked((short)(0xFE00 | il[p++]));
                OpCode op;
                if (!s_ops.TryGetValue(code, out op)) return false;
                if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch || op.FlowControl == FlowControl.Throw) return false;
                int size;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch: return false;
                    default: size = 4; break;
                }
                if (op.OperandType == OperandType.InlineMethod)
                {
                    int token = BitConverter.ToInt32(il, p);
                    MethodBase target = module.ResolveMethod(token, typeArgs, methodArgs);
                    Type decl = target.DeclaringType;
                    if (ReferenceEquals(decl, null)) return false;
                    if (ReferenceEquals(decl, typeof(ConfigFile)) && target.Name == "Bind") binds++;
                    else if (ReferenceEquals(decl, typeof(BaseUnityPlugin)) && target.Name == "get_Config")
                    {
                    }
                    else if (target.IsConstructor && decl.Namespace == "BepInEx.Configuration")
                    {
                    }
                    else if (ReferenceEquals(decl, owner) && !target.IsConstructor)
                    {
                        if (!OnlyBinds(target, owner, depth + 1, ref binds)) return false;
                    }
                    else return false;
                }
                p += size;
            }
            return true;
        }

        // ---- 2. throttles

        private void ApplyThrottles()
        {
            List<KeyValuePair<string, double>> wanted = TameList.Parse(ThrottleList);
            for (int i = 0; i < wanted.Count; i++)
            {
                string full = wanted[i].Key;
                if (m_throttled.ContainsKey(full)) continue;
                int dot = full.LastIndexOf('.');
                if (dot <= 0) continue;
                string typeName = full.Substring(0, dot);
                string methodName = full.Substring(dot + 1);
                Type type = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = a.GetType(typeName);
                    }
                    catch (Exception)
                    {
                    }
                    if (!ReferenceEquals(type, null)) break;
                }
                // Not installed on this profile (or not started yet): nothing to do, nothing to say.
                if (ReferenceEquals(type, null)) continue;
                MethodInfo method = null;
                try
                {
                    method = type.GetMethod(methodName, Any | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                }
                m_throttled[full] = true;
                if (ReferenceEquals(method, null) || !ReferenceEquals(method.ReturnType, typeof(void)))
                {
                    Log.LogWarning("Tame: [Tame] Throttle entry '" + full + "': no such method, more than one with that name, or it returns a value. Left alone.");
                    continue;
                }
                Gate g = new Gate();
                g.EveryTicks = (long)(wanted[i].Value * Stopwatch.Frequency);
                if (!Close(method, g)) continue;
                string text = full + " now runs at most once every " + wanted[i].Value.ToString("0.###", CultureInfo.InvariantCulture) + " s";
                Timeline.Write("DEF", "tame: " + text);
                Log.LogInfo("Tame: " + text);
            }
        }
    }
}
