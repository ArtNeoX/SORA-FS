using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.Flight
{
    /// A pool of camera-facing world-space lines, the equivalent of Roblox Beams.
    /// Rendered by LineRenderer so they show in the Game view, not just the Scene view.
    public class ForceBeams
    {
        readonly LineRenderer[] lines;
        readonly Transform root;

        public ForceBeams(string name, Transform parent, int count, float width)
        {
            root = new GameObject(name).transform;
            root.SetParent(parent, false);

            var material = new Material(Shader.Find("Sprites/Default"));
            lines = new LineRenderer[count];

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Beam" + i);
                go.transform.SetParent(root, false);

                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.widthMultiplier = width;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 0;
                lr.shadowCastingMode = ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.lightProbeUsage = LightProbeUsage.Off;
                lr.sharedMaterial = material;
                lines[i] = lr;
            }

            root.gameObject.SetActive(false);
        }

        public bool Visible
        {
            get => root.gameObject.activeSelf;
            set { if (root.gameObject.activeSelf != value) root.gameObject.SetActive(value); }
        }

        public void Set(int index, Vector3 from, Vector3 to, Color color)
        {
            var lr = lines[index];
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startColor = color;
            lr.endColor = color;
        }
    }
}
