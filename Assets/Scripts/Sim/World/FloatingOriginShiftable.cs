using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Explicit opt-in for an object that must travel with the world when the origin shifts.
    ///
    /// Only needed when FloatingOriginManager.autoCollectSceneRoots is off, or for an object
    /// that is not a scene root and is not parented to one that shifts - a runtime-spawned
    /// world object, for instance. Registration is automatic on enable.
    [DisallowMultipleComponent]
    public class FloatingOriginShiftable : MonoBehaviour
    {
        static readonly List<FloatingOriginShiftable> registered = new List<FloatingOriginShiftable>();

        [Tooltip("Rigidbody on this object or its children are teleported with their velocities " +
                 "preserved instead of being moved as plain transforms.")]
        public bool hasDynamicBodies;

        public static IReadOnlyList<FloatingOriginShiftable> Registered => registered;

        void OnEnable() => registered.Add(this);

        void OnDisable() => registered.Remove(this);
    }
}
