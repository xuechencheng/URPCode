using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine.Assertions;

namespace UnityEngine.Rendering
{
    using UnityObject = UnityEngine.Object;
    public sealed class VolumeManager
    {
        static readonly Lazy<VolumeManager> s_Instance = new Lazy<VolumeManager>(() => new VolumeManager());
        public static VolumeManager instance => s_Instance.Value;
        public VolumeStack stack { get; private set; }
        /// <summary>
        /// 所有继承于VolumeComponent的类
        /// </summary>
        public IEnumerable<Type> baseComponentTypes { get; private set; }
        const int k_MaxLayerCount = 32;
        readonly Dictionary<int, List<Volume>> m_SortedVolumes;
        readonly List<Volume> m_Volumes;
        readonly Dictionary<int, bool> m_SortNeeded;
        // 所有继承于VolumeComponent的实例
        readonly List<VolumeComponent> m_ComponentsDefaultState;
        readonly List<Collider> m_TempColliders;
        VolumeManager()
        {
            m_SortedVolumes = new Dictionary<int, List<Volume>>();
            m_Volumes = new List<Volume>();
            m_SortNeeded = new Dictionary<int, bool>();
            m_TempColliders = new List<Collider>(8);
            m_ComponentsDefaultState = new List<VolumeComponent>();
            ReloadBaseTypes();
            stack = CreateStack();
        }
        public VolumeStack CreateStack()
        {
            var stack = new VolumeStack();
            stack.Reload(baseComponentTypes);
            return stack;
        }
        public void DestroyStack(VolumeStack stack)
        {
            stack.Dispose();
        }
        /// <summary>
        /// 实例化所有继承自VolumeComponent的类，并加入到m_ComponentsDefaultState Done
        /// </summary>
        void ReloadBaseTypes()
        {
            m_ComponentsDefaultState.Clear();
            baseComponentTypes = CoreUtils.GetAllTypesDerivedFrom<VolumeComponent>().Where(t => !t.IsAbstract);
            foreach (var type in baseComponentTypes)
            {
                var inst = (VolumeComponent)ScriptableObject.CreateInstance(type);
                m_ComponentsDefaultState.Add(inst);
            }
        }
        /// <summary>
        /// 注册Volume
        /// </summary>
        public void Register(Volume volume, int layer)
        {
            m_Volumes.Add(volume);
            foreach (var kvp in m_SortedVolumes)
            {
                if ((kvp.Key & (1 << layer)) != 0 && !kvp.Value.Contains(volume))
                    kvp.Value.Add(volume);
            }
            SetLayerDirty(layer);
        }
        /// <summary>
        /// Unregister Volume
        /// </summary>
        /// <param name="volume"></param>
        /// <param name="layer"></param>
        public void Unregister(Volume volume, int layer)
        {
            m_Volumes.Remove(volume);
            foreach (var kvp in m_SortedVolumes)
            {
                if ((kvp.Key & (1 << layer)) == 0)
                    continue;
                kvp.Value.Remove(volume);
            }
        }
        public bool IsComponentActiveInMask<T>(LayerMask layerMask) where T : VolumeComponent
        {
            int mask = layerMask.value;
            foreach (var kvp in m_SortedVolumes)
            {
                if (kvp.Key != mask)
                    continue;
                foreach (var volume in kvp.Value)
                {
                    if (!volume.enabled || volume.profileRef == null)
                        continue;
                    if (volume.profileRef.TryGet(out T component) && component.active)
                        return true;
                }
            }
            return false;
        }
        /// <summary>
        /// 设置m_SortNeeded[mask]
        /// </summary>
        internal void SetLayerDirty(int layer)
        {
            Assert.IsTrue(layer >= 0 && layer <= k_MaxLayerCount, "Invalid layer bit");
            foreach (var kvp in m_SortedVolumes)
            {
                var mask = kvp.Key;
                if ((mask & (1 << layer)) != 0)
                    m_SortNeeded[mask] = true;
            }
        }
        /// <summary>
        /// Volume更新了Layer
        /// </summary>
        internal void UpdateVolumeLayer(Volume volume, int prevLayer, int newLayer)
        {
            Assert.IsTrue(prevLayer >= 0 && prevLayer <= k_MaxLayerCount, "Invalid layer bit");
            Unregister(volume, prevLayer);
            Register(volume, newLayer);
        }
        //Done
        void OverrideData(VolumeStack stack, List<VolumeComponent> components, float interpFactor)
        {
            foreach (var component in components)
            {
                if (!component.active)
                    continue;
                var state = stack.GetComponent(component.GetType());
                component.Override(state, interpFactor);
            }
        }
        /// <summary>
        /// 用components替换stack中的数据 Done
        /// </summary>
        void ReplaceData(VolumeStack stack, List<VolumeComponent> components)
        {
            foreach (var component in components)
            {
                var target = stack.GetComponent(component.GetType());
                int count = component.parameters.Count;
                for (int i = 0; i < count; i++)
                {
                    if(target.parameters[i] != null)
                    {
                        target.parameters[i].overrideState = false;
                        target.parameters[i].SetValue(component.parameters[i]);
                    }
                }
            }
        }
        /// <summary>
        /// 根据需要重载m_ComponentsDefaultState Done
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        public void CheckBaseTypes()
        {
            if (m_ComponentsDefaultState == null || (m_ComponentsDefaultState.Count > 0 && m_ComponentsDefaultState[0] == null))
                ReloadBaseTypes();
        }
        /// <summary>
        /// 根据需要重载stack.components Done
        /// </summary>
        /// <param name="stack"></param>
        [Conditional("UNITY_EDITOR")]
        public void CheckStack(VolumeStack stack)
        {
            var components = stack.components;
            if (components == null)
            {
                stack.Reload(baseComponentTypes);
                return;
            }
            foreach (var kvp in components)
            {
                if (kvp.Key == null || kvp.Value == null)
                {
                    stack.Reload(baseComponentTypes);
                    return;
                }
            }
        }
        public void Update(Transform trigger, LayerMask layerMask)
        {
            Update(stack, trigger, layerMask);
        }
        /// <summary>
        /// 根据Volumes进行属性插值 Done
        /// </summary>
        public void Update(VolumeStack stack, Transform trigger, LayerMask layerMask)
        {
            Assert.IsNotNull(stack);
            CheckBaseTypes();
            CheckStack(stack);
            // Start by resetting the global state to default values
            ReplaceData(stack, m_ComponentsDefaultState);
            bool onlyGlobal = trigger == null;
            var triggerPos = onlyGlobal ? Vector3.zero : trigger.position;
            // Sort the cached volume list(s) for the given layer mask if needed and return it
            var volumes = GrabVolumes(layerMask);
            Camera camera = null;
            if (!onlyGlobal)
                trigger.TryGetComponent<Camera>(out camera);
            foreach (var volume in volumes)
            {
#if UNITY_EDITOR
                if (!IsVolumeRenderedByCamera(volume, camera))
                    continue;
#endif
                if (!volume.enabled || volume.profileRef == null || volume.weight <= 0f)
                    continue;
                if (volume.isGlobal)
                {
                    OverrideData(stack, volume.profileRef.components, Mathf.Clamp01(volume.weight));
                    continue;
                }
                if (onlyGlobal)
                    continue;
                // If volume isn't global and has no collider, skip it as it's useless
                var colliders = m_TempColliders;
                volume.GetComponents(colliders);
                if (colliders.Count == 0)
                    continue;
                // Find closest distance to volume, 0 means it's inside it
                float closestDistanceSqr = float.PositiveInfinity;
                foreach (var collider in colliders)
                {
                    if (!collider.enabled)
                        continue;
                    var closestPoint = collider.ClosestPoint(triggerPos);
                    var d = (closestPoint - triggerPos).sqrMagnitude;
                    if (d < closestDistanceSqr)
                        closestDistanceSqr = d;
                }
                colliders.Clear();
                float blendDistSqr = volume.blendDistance * volume.blendDistance;
                // Volume has no influence, ignore it
                // Note: Volume doesn't do anything when `closestDistanceSqr = blendDistSqr` but we
                //       can't use a >= comparison as blendDistSqr could be set to 0 in which case
                //       volume would have total influence
                if (closestDistanceSqr > blendDistSqr)
                    continue;

                // Volume has influence
                float interpFactor = 1f;

                if (blendDistSqr > 0f)
                    interpFactor = 1f - (closestDistanceSqr / blendDistSqr);

                // No need to clamp01 the interpolation factor as it'll always be in [0;1[ range
                OverrideData(stack, volume.profileRef.components, interpFactor * Mathf.Clamp01(volume.weight));
            }
        }

        /// <summary>
        /// Get all volumes on a given layer mask sorted by influence.
        /// </summary>
        /// <param name="layerMask">The LayerMask that Unity uses to filter Volumes that it should consider.</param>
        /// <returns>An array of volume.</returns>
        public Volume[] GetVolumes(LayerMask layerMask)
        {
            var volumes = GrabVolumes(layerMask);
            return volumes.ToArray();
        }
        /// <summary>
        /// 根据LayerMask获取Volume
        /// </summary>
        List<Volume> GrabVolumes(LayerMask mask)
        {
            List<Volume> list;
            if (!m_SortedVolumes.TryGetValue(mask, out list))
            {
                list = new List<Volume>();
                foreach (var volume in m_Volumes)
                {
                    if ((mask & (1 << volume.gameObject.layer)) == 0)
                        continue;
                    list.Add(volume);
                    m_SortNeeded[mask] = true;
                }
                m_SortedVolumes.Add(mask, list);
            }
            bool sortNeeded;
            if (m_SortNeeded.TryGetValue(mask, out sortNeeded) && sortNeeded)
            {
                m_SortNeeded[mask] = false;
                SortByPriority(list);
            }
            return list;
        }

        /// <summary>
        /// 根据Priority排序 Done
        /// </summary>
        static void SortByPriority(List<Volume> volumes)
        {
            Assert.IsNotNull(volumes, "Trying to sort volumes of non-initialized layer");
            for (int i = 1; i < volumes.Count; i++)
            {
                var temp = volumes[i];
                int j = i - 1;
                // Sort order is ascending
                while (j >= 0 && volumes[j].priority > temp.priority)
                {
                    volumes[j + 1] = volumes[j];
                    j--;
                }
                volumes[j + 1] = temp;
            }
        }

        static bool IsVolumeRenderedByCamera(Volume volume, Camera camera)
        {
#if UNITY_2018_3_OR_NEWER && UNITY_EDITOR
            // IsGameObjectRenderedByCamera does not behave correctly when camera is null so we have to catch it here.
            return camera == null ? true : UnityEditor.SceneManagement.StageUtility.IsGameObjectRenderedByCamera(volume.gameObject, camera);
#else
            return true;
#endif
        }
    }

    /// <summary>
    /// A scope in which a Camera filters a Volume.
    /// </summary>
    [Obsolete("VolumeIsolationScope is deprecated, it does not have any effect anymore.")]
    public struct VolumeIsolationScope : IDisposable
    {
        /// <summary>
        /// Constructs a scope in which a Camera filters a Volume.
        /// </summary>
        /// <param name="unused">Unused parameter.</param>
        public VolumeIsolationScope(bool unused) {}

        /// <summary>
        /// Stops the Camera from filtering a Volume.
        /// </summary>
        void IDisposable.Dispose() {}
    }
}
