using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Scans the aircraft folder and builds the list the menu shows, X-Plane style: one
    /// subfolder per aircraft, each holding an aircraft.json and an icon.
    ///
    ///   StreamingAssets/Aircraft/
    ///     CirrusVision/
    ///       aircraft.json
    ///       icon.png
    ///
    /// StreamingAssets rather than Assets/Aircraft because only StreamingAssets is still a
    /// readable directory in a player build - Assets/ does not ship. The art stays where it
    /// is; aircraft.json points at it through prefab_resource_path (a prefab under a Resources
    /// folder) or scene_object (an instance already in the scene).
    ///
    /// Platform note: on Android and WebGL, StreamingAssets is not a filesystem path and this
    /// scan returns nothing. Those platforms need UnityWebRequest and a manifest file instead
    /// of a directory listing.
    public static class AircraftCatalog
    {
        public const string FolderName = "Aircraft";
        public const string ManifestFileName = "aircraft.json";

        /// Icons above this size are rejected rather than silently eating VRAM.
        const int MaxIconPixels = 4096;

        public static string RootPath => Path.Combine(Application.streamingAssetsPath, FolderName);

        /// Every installed aircraft, sorted by display name. Never null: a missing folder or a
        /// broken manifest is logged and skipped, so one bad aircraft cannot stop the menu
        /// from opening.
        public static List<AircraftEntry> LoadAll()
        {
            var entries = new List<AircraftEntry>();
            string root = RootPath;

            if (!Directory.Exists(root))
            {
                Debug.LogWarning($"[AircraftCatalog] No aircraft folder at '{root}'.");
                return entries;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AircraftCatalog] Cannot list '{root}': {e.Message}");
                return entries;
            }

            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);

            foreach (string folder in folders)
            {
                AircraftEntry entry = LoadFolder(folder);
                if (entry != null) entries.Add(entry);
            }

            entries.Sort((a, b) => string.Compare(a.Data.DisplayName, b.Data.DisplayName,
                                                  StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        /// Loads one aircraft folder, or null if it has no usable manifest.
        public static AircraftEntry LoadFolder(string folderPath)
        {
            string manifestPath = Path.Combine(folderPath, ManifestFileName);
            if (!File.Exists(manifestPath)) return null;

            AircraftData data;
            try
            {
                data = JsonUtility.FromJson<AircraftData>(File.ReadAllText(manifestPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[AircraftCatalog] '{manifestPath}' is not valid JSON: {e.Message}");
                return null;
            }

            if (data == null)
            {
                Debug.LogError($"[AircraftCatalog] '{manifestPath}' parsed to nothing.");
                return null;
            }

            // The folder name is the fallback id, so a minimal manifest still works.
            if (string.IsNullOrWhiteSpace(data.id))
            {
                data.id = new DirectoryInfo(folderPath).Name;
            }

            if (!data.IsValid(out string error))
            {
                Debug.LogError($"[AircraftCatalog] Skipping '{folderPath}': {error}");
                return null;
            }

            return new AircraftEntry(data, folderPath, LoadIcon(folderPath, data));
        }

        static Texture2D LoadIcon(string folderPath, AircraftData data)
        {
            string fileName = string.IsNullOrWhiteSpace(data.icon) ? "icon.png" : data.icon;
            string path = Path.Combine(folderPath, fileName);

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[AircraftCatalog] {data.id}: no icon at '{path}'.");
                return null;
            }

            try
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = $"{data.id}_icon",
                    wrapMode = TextureWrapMode.Clamp,
                };

                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(texture);
                    Debug.LogWarning($"[AircraftCatalog] {data.id}: '{path}' is not a readable PNG/JPG.");
                    return null;
                }

                if (texture.width > MaxIconPixels || texture.height > MaxIconPixels)
                {
                    Debug.LogWarning($"[AircraftCatalog] {data.id}: icon is {texture.width}x{texture.height}, " +
                                     $"over the {MaxIconPixels} px limit. Ignoring it.");
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                return texture;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AircraftCatalog] {data.id}: cannot read '{path}': {e.Message}");
                return null;
            }
        }

        /// Resolves the aircraft to a GameObject in the scene, if the manifest names one and it
        /// exists. Inactive objects count - that is the normal state for an aircraft waiting in
        /// the scene while the menu is up.
        public static GameObject FindSceneObject(AircraftData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.scene_object)) return null;

            // GameObject.Find skips inactive objects, so walk the roots instead.
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == data.scene_object) return root;

                Transform child = FindChildByName(root.transform, data.scene_object);
                if (child != null) return child.gameObject;
            }

            return null;
        }

        static Transform FindChildByName(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;

                Transform found = FindChildByName(child, name);
                if (found != null) return found;
            }

            return null;
        }

        /// Loads the aircraft prefab from Resources, or null if the manifest has no path or the
        /// path does not resolve.
        public static GameObject LoadPrefab(AircraftData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.prefab_resource_path)) return null;

            var prefab = Resources.Load<GameObject>(data.prefab_resource_path);
            if (prefab == null)
            {
                Debug.LogError($"[AircraftCatalog] {data.id}: no prefab at Resources/" +
                               $"{data.prefab_resource_path}.");
            }

            return prefab;
        }
    }
}
