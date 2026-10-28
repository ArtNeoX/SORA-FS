using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Contents of one aircraft folder's aircraft.json, X-Plane style: the folder is the unit
    /// of installation, and it carries its own metadata and icon.
    ///
    ///   StreamingAssets/Aircraft/CirrusVision/aircraft.json
    ///   StreamingAssets/Aircraft/CirrusVision/icon.png
    ///
    /// Field names are the JSON wire format, snake_case to match the files on disk.
    [Serializable]
    public class AircraftData
    {
        /// Stable key. Defaults to the folder name when the file leaves it out.
        public string id = string.Empty;

        public string name = string.Empty;
        public string manufacturer = string.Empty;

        /// Second line in the list, the way X-Plane shows the livery or engine variant.
        public string variant = string.Empty;

        /// Icon file name inside the aircraft folder.
        public string icon = "icon.png";

        /// Prefab under a Resources folder, e.g. "Aircraft/CirrusVision". Used when the
        /// aircraft is not already present in the scene.
        public string prefab_resource_path = string.Empty;

        /// Name of a GameObject already in the loaded scene. Takes priority over the prefab:
        /// the object is activated and repositioned rather than instantiated, which is how the
        /// current CirrusVision in Flight.unity is used.
        public string scene_object = string.Empty;

        /// Distance from the aircraft's origin down to the bottom of the tyres, metres. The
        /// spawn point is lifted by this so the gear starts just clear of the surface instead
        /// of interpenetrating it.
        public float gear_height_m = 1.0f;

        /// Extra clearance above the surface at spawn, metres. Small on purpose: the
        /// suspension settles it.
        public float spawn_clearance_m = 0.05f;

        public bool selectable = true;

        // ---- clean accessors -----------------------------------------------------

        public string Id => id;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(name)) return name;
                return string.IsNullOrEmpty(id) ? "Unnamed aircraft" : id;
            }
        }

        /// Title line, e.g. "Cirrus Vision SF50 - Cirrus".
        public string ListTitle =>
            string.IsNullOrEmpty(manufacturer) ? DisplayName : $"{DisplayName} - {manufacturer}";

        /// Subtitle line, e.g. "G2+ Default".
        public string ListSubtitle => string.IsNullOrEmpty(variant) ? "Default" : variant;

        /// Height above the runway surface the aircraft's origin is placed at.
        public float SpawnHeight => Mathf.Max(0f, gear_height_m) + Mathf.Max(0f, spawn_clearance_m);

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "aircraft has no id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(prefab_resource_path) && string.IsNullOrWhiteSpace(scene_object))
            {
                error = $"{id}: needs either prefab_resource_path or scene_object";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// One installed aircraft: its metadata, where it was loaded from, and its icon. Built by
    /// AircraftCatalog; the UI and the spawner both take this rather than a raw AircraftData,
    /// so nothing else has to know about the file layout.
    public class AircraftEntry
    {
        public AircraftData Data { get; }

        /// Absolute path of the folder the aircraft was loaded from.
        public string FolderPath { get; }

        public Texture2D Icon { get; }

        Sprite iconSprite;

        public AircraftEntry(AircraftData data, string folderPath, Texture2D icon)
        {
            Data = data;
            FolderPath = folderPath;
            Icon = icon;
        }

        public string Id => Data.Id;

        /// Icon as a Sprite for UI Image, created once on first use.
        public Sprite IconSprite
        {
            get
            {
                if (iconSprite == null && Icon != null)
                {
                    iconSprite = Sprite.Create(Icon,
                                               new Rect(0f, 0f, Icon.width, Icon.height),
                                               new Vector2(0.5f, 0.5f),
                                               100f,
                                               0,
                                               SpriteMeshType.FullRect);
                    iconSprite.name = $"{Data.Id}_icon";
                }

                return iconSprite;
            }
        }

        public override string ToString() => Data.DisplayName;
    }
}
