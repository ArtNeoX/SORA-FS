using UnityEngine;

namespace CirrusFS.Geo
{
    /// Tileable noise for runway pavement and rubber (`_RunwayDetail`, global). The terrain
    /// shader samples it in runway-local metres with explicit gradients, so the mip chain does
    /// the anti-aliasing: grain and streaks average out to their mean far away instead of
    /// shimmering.
    ///   R  white noise, one value per texel (aggregate grain, fine rubber streaks)
    ///   G  value noise, 128 cells (fine streaks)
    ///   B  value noise, 16 cells (stains, coarse rubber)
    ///   A  value noise, 32 cells (medium streaks, paint wear)
    public static class RunwayDetailTexture
    {
        public static readonly int Id = Shader.PropertyToID("_RunwayDetail");
        static Texture2D texture;

        public static Texture2D Get()
        {
            if (texture == null) texture = Create(256);
            return texture;
        }

        static Texture2D Create(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "Runway detail noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                hideFlags = HideFlags.DontSave,
            };

            var white = new float[size * size];
            var rngWhite = new System.Random(4711);
            for (int i = 0; i < white.Length; i++) white[i] = (float)rngWhite.NextDouble();

            int[] cells = { 128, 16, 32 };
            var grids = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                var rng = new System.Random(911 + c);
                grids[c] = new float[cells[c] * cells[c]];
                for (int i = 0; i < grids[c].Length; i++) grids[c][i] = (float)rng.NextDouble();
            }

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte Value(int c) => (byte)(Mathf.Clamp01(Sample(grids[c], cells[c], x, y, size)) * 255f);
                    pixels[y * size + x] = new Color32((byte)(white[y * size + x] * 255f), Value(0), Value(1), Value(2));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        static float Sample(float[] grid, int cells, int px, int py, int size)
        {
            float fx = (px + 0.5f) / size * cells - 0.5f, fy = (py + 0.5f) / size * cells - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float Get(int gx, int gy) => grid[((gy % cells + cells) % cells) * cells + ((gx % cells + cells) % cells)];
            float a = Mathf.Lerp(Get(x0, y0), Get(x0 + 1, y0), tx);
            float b = Mathf.Lerp(Get(x0, y0 + 1), Get(x0 + 1, y0 + 1), tx);
            return Mathf.Lerp(a, b, ty);
        }
    }
}
