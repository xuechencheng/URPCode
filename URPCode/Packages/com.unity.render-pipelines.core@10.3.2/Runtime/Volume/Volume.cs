using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// A generic Volume component holding a <see cref="VolumeProfile"/>.
    /// </summary>
    /// Done
    [HelpURL(Documentation.baseURLHDRP + Documentation.version + Documentation.subURL + "Volumes" + Documentation.endURL)]
    [ExecuteAlways]
    [AddComponentMenu("Miscellaneous/Volume")]
    public class Volume : MonoBehaviour
    {
        /// <summary>
        /// Specifies whether to apply the Volume to the entire Scene or not.
        /// </summary>
        [Tooltip("When enabled, HDRP applies this Volume to the entire Scene.")]
        public bool isGlobal = true;
        /// <summary>
        /// The Volume priority in the stack. A higher value means higher priority. This supports negative values.
        /// </summary>
        [Tooltip("Sets the Volume priority in the stack. A higher value means higher priority. You can use negative values.")]
        public float priority = 0f;
        /// <summary>
        /// The outer distance to start blending from. A value of 0 means no blending and Unity applies
        /// the Volume overrides immediately upon entry.
        /// </summary>
        [Tooltip("Sets the outer distance to start blending from. A value of 0 means no blending and Unity applies the Volume overrides immediately upon entry.")]
        public float blendDistance = 0f;
        /// <summary>
        /// The total weight of this volume in the Scene. 0 means no effect and 1 means full effect.
        /// </summary>
        [Range(0f, 1f), Tooltip("Sets the total weight of this Volume in the Scene. 0 means no effect and 1 means full effect.")]
        public float weight = 1f;
        public VolumeProfile sharedProfile = null;
        public VolumeProfile profile
        {
            get
            {
                if (m_InternalProfile == null)
                {
                    m_InternalProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                    if (sharedProfile != null)
                    {
                        foreach (var item in sharedProfile.components)
                        {
                            var itemCopy = Instantiate(item);
                            m_InternalProfile.components.Add(itemCopy);
                        }
                    }
                }
                return m_InternalProfile;
            }
            set => m_InternalProfile = value;
        }

        internal VolumeProfile profileRef => m_InternalProfile == null ? sharedProfile : m_InternalProfile;

        public bool HasInstantiatedProfile() => m_InternalProfile != null;

        int m_PreviousLayer;
        float m_PreviousPriority;
        VolumeProfile m_InternalProfile;

        void OnEnable()
        {
            m_PreviousLayer = gameObject.layer;
            VolumeManager.instance.Register(this, m_PreviousLayer);
        }

        void OnDisable()
        {
            VolumeManager.instance.Unregister(this, gameObject.layer);
        }

        void Update()
        {
            UpdateLayer();
            if (priority != m_PreviousPriority)
            {
                VolumeManager.instance.SetLayerDirty(gameObject.layer);
                m_PreviousPriority = priority;
            }
        }
        /// <summary>
        /// ¸üÐÂVolumeµÄLayer
        /// </summary>
        internal void UpdateLayer()
        {
            int layer = gameObject.layer;
            if (layer != m_PreviousLayer)
            {
                VolumeManager.instance.UpdateVolumeLayer(this, m_PreviousLayer, layer);
                m_PreviousLayer = layer;
            }
        }
#if UNITY_EDITOR
        // TODO: Look into a better volume previsualization system
        List<Collider> m_TempColliders;

        void OnDrawGizmos()
        {
            if (m_TempColliders == null)
                m_TempColliders = new List<Collider>();
            var colliders = m_TempColliders;
            GetComponents(colliders);
            if (isGlobal || colliders == null)
                return;
            var scale = transform.localScale;
            var invScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, scale);
            Gizmos.color = CoreRenderPipelinePreferences.volumeGizmoColor;
            // Draw a separate gizmo for each collider
            foreach (var collider in colliders)
            {
                if (!collider.enabled)
                    continue;
                
                switch (collider)
                {
                    case BoxCollider c:
                        Gizmos.DrawCube(c.center, c.size);
                        break;
                    case SphereCollider c:
                        // For sphere the only scale that is used is the transform.x
                        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one * scale.x);
                        Gizmos.DrawSphere(c.center, c.radius);
                        break;
                    case MeshCollider c:
                        // Only convex mesh m_Colliders are allowed
                        if (!c.convex)
                            c.convex = true;
                        // Mesh pivot should be centered or this won't work
                        Gizmos.DrawMesh(c.sharedMesh);
                        break;
                    default:
                        // Nothing for capsule (DrawCapsule isn't exposed in Gizmo), terrain, wheel and
                        // other m_Colliders...
                        break;
                }
            }
            colliders.Clear();
        }
#endif
    }
}
