using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    /// First Done
    public sealed class VolumeStack : IDisposable
    {
        // Holds the state of _all_ component types you can possibly add on volumes
        internal Dictionary<Type, VolumeComponent> components;

        internal VolumeStack()
        {
        }
        /// <summary>
        /// 用baseTypes的实例来初始化components Done
        /// </summary>
        /// <param name="baseTypes"></param>
        internal void Reload(IEnumerable<Type> baseTypes)
        {
            if (components == null)
                components = new Dictionary<Type, VolumeComponent>();
            else
                components.Clear();

            foreach (var type in baseTypes)
            {
                var inst = (VolumeComponent)ScriptableObject.CreateInstance(type);
                components.Add(type, inst);
            }
        }

        /// <summary>
        /// Gets the current state of the <see cref="VolumeComponent"/> of type <typeparamref name="T"/>
        /// in the stack.
        /// </summary>
        /// <typeparam name="T">A type of <see cref="VolumeComponent"/>.</typeparam>
        /// <returns>The current state of the <see cref="VolumeComponent"/> of type <typeparamref name="T"/>
        /// in the stack.</returns>
        /// First Done
        public T GetComponent<T>() where T : VolumeComponent
        {
            var comp = GetComponent(typeof(T));
            return (T)comp;
        }

        public VolumeComponent GetComponent(Type type)
        {
            components.TryGetValue(type, out var comp);
            return comp;
        }

        /// <summary>
        /// Cleans up the content of this stack. Once a <c>VolumeStack</c> is disposed, it souldn't
        /// be used anymore.
        /// </summary>
        public void Dispose()
        {
            foreach (var component in components)
                CoreUtils.Destroy(component.Value);

            components.Clear();
        }
    }
}
