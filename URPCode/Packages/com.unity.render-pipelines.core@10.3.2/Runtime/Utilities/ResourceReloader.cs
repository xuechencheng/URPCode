using System;
using System.IO;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor;
using System.Reflection;
#endif

namespace UnityEngine.Rendering
{
#if UNITY_EDITOR
    /// <summary>
    /// The resources that need to be reloaded in Editor can live in Runtime.
    /// The reload call should only be done in Editor context though but it
    /// could be called from runtime entities.
    /// </summary>
    public static class ResourceReloader
    {
        /// <summary>
        /// ��ʼ��ReloadGroup��ǩ�����У�����Reload��ǩ���ֶΡ�Done
        /// </summary>
        public static (bool hasChange, bool assetDatabaseNotReady) TryReloadAllNullIn(System.Object container, string basePath)
        {
            try
            {
                return (ReloadAllNullIn(container, basePath), false);
            }
            catch (Exception e)
            {
                if (!(e.Data.Contains("InvalidImport") && e.Data["InvalidImport"] is int && (int)e.Data["InvalidImport"] == 1))
                    throw e;
                return (false, true);
            }
        }


        /// <summary>
        /// ��ʼ��ReloadGroup��ǩ�����У�����Reload��ǩ���ֶΡ� Done
        /// </summary>
        public static bool ReloadAllNullIn(System.Object container, string basePath)
        {
            if (IsNull(container))
                return false;
            var changed = false;
            foreach (var fieldInfo in container.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (IsReloadGroup(fieldInfo))
                {
                    changed |= FixGroupIfNeeded(container, fieldInfo);
                    changed |= ReloadAllNullIn(fieldInfo.GetValue(container), basePath);
                }
                var attribute = GetReloadAttribute(fieldInfo);
                if (attribute != null)
                {
                    if (attribute.paths.Length == 1)
                    {
                        changed |= SetAndLoadIfNull(container, fieldInfo, GetFullPath(basePath, attribute), attribute.package == ReloadAttribute.Package.Builtin);
                    }
                    else if (attribute.paths.Length > 1)
                    {
                        changed |= FixArrayIfNeeded(container, fieldInfo, attribute.paths.Length);
                        var array = (Array)fieldInfo.GetValue(container);
                        if (IsReloadGroup(array))
                        {
                            for (int index = 0; index < attribute.paths.Length; ++index)
                            {
                                changed |= FixGroupIfNeeded(array, index);
                                changed |= ReloadAllNullIn(array.GetValue(index), basePath);
                            }
                        }
                        else
                        {
                            bool builtin = attribute.package == ReloadAttribute.Package.Builtin;
                            for (int index = 0; index < attribute.paths.Length; ++index)
                                changed |= SetAndLoadIfNull(array, index, GetFullPath(basePath, attribute, index), builtin);
                        }
                    }
                }
            }
            if (changed && container is UnityEngine.Object c)
                EditorUtility.SetDirty(c);
            return changed;
        }

        /// <summary>
        /// ���FieldInfoΪ�գ�ʵ����һ��FieldInfo������container Done
        /// </summary>
        static bool FixGroupIfNeeded(System.Object container, FieldInfo info)
        {
            if (IsNull(container, info))
            {
                var type = info.FieldType;
                var value = type.IsSubclassOf(typeof(ScriptableObject)) ? ScriptableObject.CreateInstance(type) : Activator.CreateInstance(type);
                info.SetValue( container, value);
                return true;
            }
            return false;
        }

        /// <summary>
        /// ���贴������ֵ����Ԫ�� Done
        /// </summary>
        static bool FixGroupIfNeeded(Array array, int index)
        {
            Assert.IsNotNull(array);
            if (IsNull(array.GetValue(index)))
            {
                var type = array.GetType().GetElementType();
                var value = type.IsSubclassOf(typeof(ScriptableObject)) ? ScriptableObject.CreateInstance(type) : Activator.CreateInstance(type);
                array.SetValue( value, index);
                return true;
            }
            return false;
        }
        /// <summary>
        /// ���贴��FieldInfo���鲢��ֵ Done
        /// </summary>
        static bool FixArrayIfNeeded(System.Object container, FieldInfo info, int length)
        {
            if (IsNull(container, info) || ((Array)info.GetValue(container)).Length < length)
            {
                info.SetValue( container, Activator.CreateInstance(info.FieldType, length));
                return true;
            }
            return false;
        }
        /// <summary>
        /// ��ȡ����Reload��ǩ������ Done
        /// </summary>
        static ReloadAttribute GetReloadAttribute(FieldInfo fieldInfo)
        {
            var attributes = (ReloadAttribute[])fieldInfo.GetCustomAttributes(typeof(ReloadAttribute), false);
            if (attributes.Length == 0)
                return null;
            return attributes[0];
        }

        /// <summary>
        /// ReloadGroup��ǩ��ͨ�������� Done
        /// </summary>
        static bool IsReloadGroup(FieldInfo info) => info.FieldType.GetCustomAttributes(typeof(ReloadGroupAttribute), false).Length > 0;

        /// <summary>
        /// ReloadGroup��ǩ��ͨ��������
        /// </summary>
        static bool IsReloadGroup(Array field) => field.GetType().GetElementType().GetCustomAttributes(typeof(ReloadGroupAttribute), false).Length > 0;

        // First Done
        static bool IsNull(System.Object container, FieldInfo info) => IsNull(info.GetValue(container));
        // Done
        static bool IsNull(System.Object field) => field == null || field.Equals(null);
        /// <summary>
        /// Load Asset. Done
        /// </summary>
        static UnityEngine.Object Load(string path, Type type, bool builtin)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!builtin && String.IsNullOrEmpty(guid))
                throw new Exception($"Cannot load. Incorrect path: {path}");
            UnityEngine.Object result;
            if (builtin && type == typeof(Shader))
                result = Shader.Find(path);
            else
                result = AssetDatabase.LoadAssetAtPath(path, type);
            if (IsNull(result))
            {
                var e = new Exception($"Cannot load. Path {path} is correct but AssetDatabase cannot load now.");
                e.Data["InvalidImport"] = 1;
                throw e;
            }
            return result;
        }

        /// <summary>
        /// ���FieldInfoΪ�գ��ͼ��ز������� Done
        /// </summary>
        static bool SetAndLoadIfNull(System.Object container, FieldInfo info, string path, bool builtin)
        {
            if (IsNull(container, info))
            {
                info.SetValue(container, Load(path, info.FieldType, builtin));
                return true;
            }
            return false;
        }

        /// <summary>
        /// ������ز���ֵ����Ԫ��
        /// </summary>
        static bool SetAndLoadIfNull(Array array, int index, string path, bool builtin)
        {
            var element = array.GetValue(index);
            if (IsNull(element))
            {
                array.SetValue(Load(path, array.GetType().GetElementType(), builtin), index);
                return true;
            }
            return false;
        }
        /// <summary>
        /// ��ȡ·�� Done
        /// </summary>
        static string GetFullPath(string basePath, ReloadAttribute attribute, int index = 0)
        {
            string path;
            switch (attribute.package)
            {
                case ReloadAttribute.Package.Builtin:
                    path = attribute.paths[index];
                    break;
                case ReloadAttribute.Package.Root:
                    path = basePath + "/" + attribute.paths[index];
                    break;
                default:
                    throw new ArgumentException("Unknown Package Path!");
            }
            return path;
        }
    }
#endif

    /// <summary>
    /// Attribute specifying information to reload with <see cref="ResourceReloader"/>. This is only
    /// used in the editor and doesn't have any effect at runtime.
    /// </summary>
    /// <seealso cref="ResourceReloader"/>
    /// <seealso cref="ReloadGroupAttribute"/>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ReloadAttribute : Attribute
    {
        /// <summary>
        /// Lookup method for a resource.
        /// </summary>
        public enum Package
        {
            /// <summary>
            /// Used for builtin resources when the resource isn't part of the package (i.e. builtin
            /// shaders).
            /// </summary>
            Builtin,

            /// <summary>
            /// Used for resources inside the package.
            /// </summary>
            Root
        };

#if UNITY_EDITOR
        /// <summary>
        /// The lookup method.
        /// </summary>
        public readonly Package package;

        /// <summary>
        /// Search paths.
        /// </summary>
        public readonly string[] paths;
#endif

        /// <summary>
        /// Creates a new <see cref="ReloadAttribute"/> for an array by specifying each resource
        /// path individually.
        /// </summary>
        /// <param name="paths">Search paths</param>
        /// <param name="package">The lookup method</param>
        public ReloadAttribute(string[] paths, Package package = Package.Root)
        {
#if UNITY_EDITOR
            this.paths = paths;
            this.package = package;
#endif
        }

        /// <summary>
        /// Creates a new <see cref="ReloadAttribute"/> for a single resource.
        /// </summary>
        /// <param name="path">Search path</param>
        /// <param name="package">The lookup method</param>
        public ReloadAttribute(string path, Package package = Package.Root)
            : this(new[] { path }, package)
        { }

        /// <summary>
        /// Creates a new <see cref="ReloadAttribute"/> for an array using automatic path name
        /// numbering.
        /// </summary>
        /// <param name="pathFormat">The format used for the path</param>
        /// <param name="rangeMin">The array start index (inclusive)</param>
        /// <param name="rangeMax">The array end index (exclusive)</param>
        /// <param name="package">The lookup method</param>
        public ReloadAttribute(string pathFormat, int rangeMin, int rangeMax,
            Package package = Package.Root)
        {
#if UNITY_EDITOR
            this.package = package;
            paths = new string[rangeMax - rangeMin];
            for (int index = rangeMin, i = 0; index < rangeMax; ++index, ++i)
                paths[i] = string.Format(pathFormat, index);
#endif
        }
    }

    /// <summary>
    /// Attribute specifying that it contains element that should be reloaded.
    /// If the instance of the class is null, the system will try to recreate
    /// it with the default constructor.
    /// Be sure classes using it have default constructor!
    /// </summary>
    /// <seealso cref="ReloadAttribute"/>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ReloadGroupAttribute : Attribute
    { }
}