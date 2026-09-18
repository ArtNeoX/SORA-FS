using UnityEngine;

namespace CirrusFS.Sim
{
    /// Put this on a scene root that must NOT move when the origin shifts: anything already
    /// expressed in aircraft-relative or screen space, such as the menu canvas, an
    /// EventSystem, or a manager object.
    ///
    /// The aircraft itself does not need it - the manager always handles the tracked aircraft
    /// separately.
    [DisallowMultipleComponent]
    public class FloatingOriginIgnore : MonoBehaviour
    {
    }
}
