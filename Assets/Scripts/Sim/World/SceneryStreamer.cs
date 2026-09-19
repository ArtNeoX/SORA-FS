using UnityEngine;

namespace CirrusFS.Sim
{
    /// Turns distant scenery off and near scenery back on.
    ///
    /// It drives every SceneryRegion from one place rather than having each one check itself,
    /// because a culled scenery has to be able to come back - and an object that has disabled
    /// itself no longer runs any code to notice the aircraft returning. Only renderers and
    /// colliders are switched, so the roots stay findable and measurable throughout.
    ///
    /// Distance is measured horizontally to the nearest point of a scenery's bounds, so a region
    /// the size of Japan counts as near while you are anywhere over it.
    [DisallowMultipleComponent]
    public class SceneryStreamer : MonoBehaviour
    {
        [Tooltip("What to measure from. Falls back to the tracked aircraft, then the camera.")]
        public Transform viewer;

        public FloatingOriginManager floatingOrigin;

        [Tooltip("Seconds between checks. Scenery is kilometres across, so this does not need to " +
                 "run every frame.")]
        public float checkInterval = 0.5f;

        [Tooltip("Extra metres a scenery must move beyond its cull distance before it is turned " +
                 "off, so one sitting exactly on the boundary does not flicker.")]
        public float hysteresisM = 2000f;

        [Tooltip("Log every time a scenery comes or goes.")]
        public bool logChanges;

        float nextCheck;

        void Awake()
        {
            if (floatingOrigin == null) floatingOrigin = FindFirstObjectByType<FloatingOriginManager>();
        }

        void LateUpdate()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + Mathf.Max(0.05f, checkInterval);

            Refresh();
        }

        /// Runs one pass immediately. Public so a spawn or a teleport can settle the world
        /// without waiting for the next tick.
        public int Refresh()
        {
            Transform from = ResolveViewer();
            if (from == null) return 0;

            Vector3 point = from.position;
            int changed = 0;

            foreach (SceneryRegion scenery in SceneryRegion.FindAll())
            {
                if (scenery == null) continue;

                bool wanted = ShouldBeVisible(scenery, point);
                if (wanted == scenery.ContentVisible) continue;

                scenery.SetContentVisible(wanted);
                changed++;

                if (logChanges)
                {
                    Debug.Log($"[SceneryStreamer] {scenery.sceneryId} {(wanted ? "in" : "out")} at " +
                              $"{scenery.HorizontalDistanceTo(point) / 1000f:F1} km.", scenery);
                }
            }

            return changed;
        }

        public bool ShouldBeVisible(SceneryRegion scenery, Vector3 point)
        {
            if (scenery.alwaysLoaded || scenery.renderDistanceM <= 0f) return true;

            float distance = scenery.HorizontalDistanceTo(point);

            // Asymmetric thresholds: it has to get properly far away before it goes, and come
            // properly close before it returns.
            return scenery.ContentVisible
                ? distance <= scenery.renderDistanceM + hysteresisM
                : distance <= scenery.renderDistanceM;
        }

        Transform ResolveViewer()
        {
            if (viewer != null) return viewer;
            if (floatingOrigin != null && floatingOrigin.trackedAircraft != null) return floatingOrigin.trackedAircraft;

            Camera camera = Camera.main;
            return camera != null ? camera.transform : null;
        }
    }
}
