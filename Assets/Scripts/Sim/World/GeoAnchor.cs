using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// A point inside a scenery model whose real-world coordinate is known, declared by naming
    /// the object after it:
    ///
    ///     35.556428, 139.772047
    ///
    /// The modeller already knows where a given point really is - they put the empty there on
    /// purpose - so the model carries its own georeference and nothing has to be worked out
    /// backwards from its geometry.
    ///
    /// Only the object's **position** is read. An empty exported from Blender arrives with a
    /// display size baked in as scale and often some arbitrary rotation; neither means anything
    /// here.
    public readonly struct GeoAnchor
    {
        /// Latitude and longitude, both written with a dot as the decimal point and separated by
        /// a comma or a space. Both numbers must have a decimal point, which is what keeps a
        /// name like "Pad 3" from being read as a coordinate.
        static readonly Regex CoordinatePattern = new Regex(
            @"(-?\d+\.\d+)\s*[, ]\s*(-?\d+\.\d+)", RegexOptions.Compiled);

        public readonly Transform Marker;
        public readonly double Latitude;
        public readonly double Longitude;

        public GeoAnchor(Transform marker, double latitude, double longitude)
        {
            Marker = marker;
            Latitude = latitude;
            Longitude = longitude;
        }

        public bool IsValid => Marker != null;

        public Vector3 WorldPosition => Marker.position;

        public override string ToString() =>
            $"'{(Marker != null ? Marker.name : "<none>")}' at {Latitude:F6}, {Longitude:F6}";

        /// Reads a coordinate out of an object's name, or returns false if there is not one.
        ///
        /// Parsing is always invariant-culture: this machine formats numbers with a decimal
        /// comma, and parsing "35.556428" under that culture would quietly yield 35556428.
        public static bool TryParse(Transform marker, out GeoAnchor anchor)
        {
            anchor = default;
            if (marker == null) return false;

            Match match = CoordinatePattern.Match(marker.name);
            if (!match.Success) return false;

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double latitude) ||
                !double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double longitude))
            {
                return false;
            }

            if (!GeoMath.IsValidLatitude(latitude) || !GeoMath.IsValidLongitude(longitude))
            {
                Debug.LogWarning($"[GeoAnchor] '{marker.name}' parses as {latitude}, {longitude}, which is " +
                                 "out of range. Expected \"<latitude>, <longitude>\" in degrees.", marker);
                return false;
            }

            anchor = new GeoAnchor(marker, latitude, longitude);
            return true;
        }

        /// Every anchor under a root, in hierarchy order.
        public static List<GeoAnchor> FindAll(Transform root)
        {
            var found = new List<GeoAnchor>();
            if (root == null) return found;

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != root && TryParse(child, out GeoAnchor anchor)) found.Add(anchor);
            }

            return found;
        }
    }
}
