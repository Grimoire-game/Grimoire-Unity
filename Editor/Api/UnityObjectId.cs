using System;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Session-local identity for a <see cref="UnityEngine.Object"/>.
    /// Unity 6.4+ uses <c>EntityId</c>; older editors still use the integer InstanceID.
    /// </summary>
    internal readonly struct UnityObjectId : IEquatable<UnityObjectId>
    {
#if UNITY_6000_4_OR_NEWER
        private readonly EntityId _value;

        public UnityObjectId(EntityId value) => _value = value;

        public static UnityObjectId Of(UnityEngine.Object obj) =>
            new UnityObjectId(obj.GetEntityId());
#else
        private readonly int _value;

        public UnityObjectId(int value) => _value = value;

        public static UnityObjectId Of(UnityEngine.Object obj) =>
            new UnityObjectId(obj.GetInstanceID());
#endif

        public bool IsValid => !_value.Equals(default);

        public bool Equals(UnityObjectId other) => _value.Equals(other._value);

        public override bool Equals(object obj) => obj is UnityObjectId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => _value.ToString();

        public static bool operator ==(UnityObjectId left, UnityObjectId right) => left.Equals(right);

        public static bool operator !=(UnityObjectId left, UnityObjectId right) => !left.Equals(right);
    }
}
