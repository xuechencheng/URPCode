// TProfilingSampler<TEnum>.samples should just be an array. Unfortunately, Enum cannot be converted to int without generating garbage.
// This could be worked around by using Unsafe but it's not available at the moment.
// So in the meantime we use a Dictionary with a perf hit...
//#define USE_UNSAFE

#if UNITY_2020_1_OR_NEWER
#define UNITY_USE_RECORDER
#endif

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.Profiling;


namespace UnityEngine.Rendering
{
    // Done
    class TProfilingSampler<TEnum> : ProfilingSampler where TEnum : Enum
    {
#if USE_UNSAFE
        internal static TProfilingSampler<TEnum>[] samples;
#else
        internal static Dictionary<TEnum, TProfilingSampler<TEnum>> samples = new Dictionary<TEnum, TProfilingSampler<TEnum>>();
#endif
        /// <summary>
        /// Done
        /// </summary>
        static TProfilingSampler()
        {
            var names = Enum.GetNames(typeof(TEnum));
#if USE_UNSAFE
            var values = Enum.GetValues(typeof(TEnum)).Cast<int>().ToArray();
            samples = new TProfilingSampler<TEnum>[values.Max() + 1];
#else
            var values = Enum.GetValues(typeof(TEnum));
#endif
            for (int i = 0; i < names.Length; i++)
            {
                var sample = new TProfilingSampler<TEnum>(names[i]);
#if USE_UNSAFE
                samples[values[i]] = sample;
#else
                samples.Add((TEnum)values.GetValue(i), sample);
#endif
            }
        }
        // Done
        public TProfilingSampler(string name): base(name)
        {
        }
    }
    // Done
    public class ProfilingSampler
    {
        internal CustomSampler sampler { get; private set; }
        internal CustomSampler inlineSampler { get; private set; }
        public string name { get; private set; }
       
        /// <summary>
        /// Done
        /// </summary>
        public ProfilingSampler(string name)
        {
#if UNITY_USE_RECORDER
            sampler = CustomSampler.Create(name, true);
#else
            sampler = CustomSampler.Create($"Dummy_{name}");
#endif
            inlineSampler = CustomSampler.Create($"Inl_{name}"); //$的作用类似于string.Format
            this.name = name;
#if UNITY_USE_RECORDER
            m_Recorder = sampler.GetRecorder();
            m_Recorder.enabled = false;
            m_InlineRecorder = inlineSampler.GetRecorder();
            m_InlineRecorder.enabled = false;
#endif
        }

        // Done
        public static ProfilingSampler Get<TEnum>(TEnum marker) where TEnum : Enum
        {
#if USE_UNSAFE
            return TProfilingSampler<TEnum>.samples[Unsafe.As<TEnum, int>(ref marker)];
#else
            TProfilingSampler<TEnum>.samples.TryGetValue(marker, out var sampler);
            return sampler;
#endif
        }

        /// <summary>
        /// BeginSample Done
        /// </summary>
        public void Begin(CommandBuffer cmd)
        {
            if (cmd != null)
#if UNITY_USE_RECORDER
                if (sampler != null && sampler.isValid)
                    cmd.BeginSample(sampler);
                else
                    cmd.BeginSample(name);
#else
                cmd.BeginSample(name);
#endif
            inlineSampler?.Begin();
        }
        /// <summary>
        /// EndSample Done
        /// </summary>
        public void End(CommandBuffer cmd)
        {
            if (cmd != null)
#if UNITY_USE_RECORDER
                if (sampler != null && sampler.isValid)
                    cmd.EndSample(sampler);
                else
                    cmd.EndSample(name);
#else
                    m_Cmd.EndSample(name);
#endif
            inlineSampler?.End();
        }

        internal bool IsValid() { return (sampler != null && inlineSampler != null); }
        
#if UNITY_USE_RECORDER
        Recorder m_Recorder;
        Recorder m_InlineRecorder;
#endif
        // Done
        public bool enableRecording
        {
            set
            {
#if UNITY_USE_RECORDER
                m_Recorder.enabled = value;
                m_InlineRecorder.enabled = value;
#endif
            }
        }

#if UNITY_USE_RECORDER
        /// <summary>
        /// 获取三帧前的一帧累积GPU时间，单位为纳秒。记录器有三帧延迟。
        /// </summary>
        public float gpuElapsedTime => m_Recorder.enabled ? m_Recorder.gpuElapsedNanoseconds / 1000000.0f : 0.0f;
        /// <summary>
        /// 获取GPU在一帧(三帧前)中执行的开始/结束时间对的数量。
        /// </summary>
        public int gpuSampleCount => m_Recorder.enabled ? m_Recorder.gpuSampleBlockCount : 0;
        /// <summary>
        /// 前一帧的 Begin/End 对的累积时间（以纳秒为单位）。
        /// </summary>
        public float cpuElapsedTime => m_Recorder.enabled ? m_Recorder.elapsedNanoseconds / 1000000.0f : 0.0f;
        /// <summary>
        /// 在前一帧中调用 Begin/End 对的次数。
        /// </summary>
        public int cpuSampleCount => m_Recorder.enabled ? m_Recorder.sampleBlockCount : 0;
        public float inlineCpuElapsedTime => m_InlineRecorder.enabled ? m_InlineRecorder.elapsedNanoseconds / 1000000.0f : 0.0f;
        public int inlineCpuSampleCount => m_InlineRecorder.enabled ? m_InlineRecorder.sampleBlockCount : 0;
#else
        /// <summary>
        /// GPU Elapsed time in milliseconds.
        /// </summary>
        public float gpuElapsedTime => 0.0f;
        /// <summary>
        /// Number of times the Profiling Sampler has hit on the GPU
        /// </summary>
        public int gpuSampleCount => 0;
        /// <summary>
        /// CPU Elapsed time in milliseconds (Command Buffer execution).
        /// </summary>
        public float cpuElapsedTime => 0.0f;
        /// <summary>
        /// Number of times the Profiling Sampler has hit on the CPU in the command buffer.
        /// </summary>
        public int cpuSampleCount => 0;
        /// <summary>
        /// CPU Elapsed time in milliseconds (Direct execution).
        /// </summary>
        public float inlineCpuElapsedTime => 0.0f;
        /// <summary>
        /// Number of times the Profiling Sampler has hit on the CPU.
        /// </summary>
        public int inlineCpuSampleCount => 0;
#endif
        // Keep the constructor private
        ProfilingSampler() { }
    }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
    // Done
    public struct ProfilingScope : IDisposable
    {
        CommandBuffer       m_Cmd;
        bool                m_Disposed;
        ProfilingSampler    m_Sampler;
        /// <summary>
        /// 开始采样 Done
        /// </summary>
        public ProfilingScope(CommandBuffer cmd, ProfilingSampler sampler)
        {
            m_Cmd = cmd;
            m_Disposed = false;
            m_Sampler = sampler;
            m_Sampler?.Begin(m_Cmd);
        }
        /// <summary>
        ///  停止采样 Done
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// 停止采样 Done
        /// </summary>
        /// <param name="disposing"></param>
        void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;
            if (disposing)
            {
                m_Sampler?.End(m_Cmd);
            }
            m_Disposed = true;
        }
}
#else
    /// <summary>
    /// Scoped Profiling markers
    /// </summary>
    public struct ProfilingScope : IDisposable
    {
        /// <summary>
        /// Profiling Scope constructor
        /// </summary>
        /// <param name="cmd">Command buffer used to add markers and compute execution timings.</param>
        /// <param name="sampler">Profiling Sampler to be used for this scope.</param>
        public ProfilingScope(CommandBuffer cmd, ProfilingSampler sampler)
        {

        }

        /// <summary>
        ///  Dispose pattern implementation
        /// </summary>
        public void Dispose()
        {
        }
    }
#endif


    /// <summary>
    /// Profiling Sampler class.
    /// </summary>
    [System.Obsolete("Please use ProfilingScope")]
    public struct ProfilingSample : IDisposable
    {
        readonly CommandBuffer m_Cmd;
        readonly string m_Name;

        bool m_Disposed;
        CustomSampler m_Sampler;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="cmd">Command Buffer.</param>
        /// <param name="name">Name of the profiling sample.</param>
        /// <param name="sampler">Custom sampler for CPU profiling.</param>
        public ProfilingSample(CommandBuffer cmd, string name, CustomSampler sampler = null)
        {
            m_Cmd = cmd;
            m_Name = name;
            m_Disposed = false;
            if (cmd != null && name != "")
                cmd.BeginSample(name);
            m_Sampler = sampler;
            m_Sampler?.Begin();
        }

        // Shortcut to string.Format() using only one argument (reduces Gen0 GC pressure)
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="cmd">Command Buffer.</param>
        /// <param name="format">Formating of the profiling sample.</param>
        /// <param name="arg">Parameters for formating the name.</param>
        public ProfilingSample(CommandBuffer cmd, string format, object arg) : this(cmd, string.Format(format, arg))
        {
        }

        // Shortcut to string.Format() with variable amount of arguments - for performance critical
        // code you should pre-build & cache the marker name instead of using this
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="cmd">Command Buffer.</param>
        /// <param name="format">Formating of the profiling sample.</param>
        /// <param name="args">Parameters for formating the name.</param>
        public ProfilingSample(CommandBuffer cmd, string format, params object[] args) : this(cmd, string.Format(format, args))
        {
        }

        /// <summary>
        ///  Dispose pattern implementation
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        // Protected implementation of Dispose pattern.
        void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;

            // As this is a struct, it could have been initialized using an empty constructor so we
            // need to make sure `cmd` isn't null to avoid a crash. Switching to a class would fix
            // this but will generate garbage on every frame (and this struct is used quite a lot).
            if (disposing)
            {
                if (m_Cmd != null && m_Name != "")
                    m_Cmd.EndSample(m_Name);
                m_Sampler?.End();
            }

            m_Disposed = true;
        }
    }
}
