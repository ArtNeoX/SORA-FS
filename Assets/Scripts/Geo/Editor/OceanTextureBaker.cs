using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CirrusFS.Geo.EditorTools
{
    /// Bakes the two tiling wave normal maps the ocean shader scrolls.
    ///
    /// Each map is a height field made of cosine waves whose wave vectors are whole numbers of
    /// cycles per tile, so it tiles exactly; the normal is the analytic gradient, so there is
    /// no finite-difference blur. Map A is short, choppy wind waves; map B is longer swell
    /// from a different direction. Amplitude falls with wavenumber like a rough ocean spectrum.
    public static class OceanTextureBaker
    {
        public const string Folder = "Assets/Textures/Ocean";
        public const string PathA = Folder + "/WaveNormal_A.png";
        public const string PathB = Folder + "/WaveNormal_B.png";

        [MenuItem("CirrusFS/Ocean/Bake Wave Normal Maps")]
        public static void BakeAll()
        {
            Directory.CreateDirectory(Folder);

            Bake(PathA, size: 512, seed: 11, waves: 96, minK: 2, maxK: 28, windDeg: 25f, spread: 70f, slope: 0.85f);
            Bake(PathB, size: 512, seed: 29, waves: 48, minK: 1, maxK: 10, windDeg: 140f, spread: 35f, slope: 0.55f);

            AssetDatabase.Refresh();
            ConfigureNormalMap(PathA);
            ConfigureNormalMap(PathB);

            Debug.Log($"[OceanTextureBaker] Wrote {PathA} and {PathB}.");
        }

        static void Bake(string path, int size, int seed, int waves, int minK, int maxK,
                         float windDeg, float spread, float slope)
        {
            var random = new System.Random(seed);
            var kx = new int[waves];
            var ky = new int[waves];
            var amp = new double[waves];
            var phase = new double[waves];

            double wind = windDeg * Math.PI / 180.0;
            double spreadRad = spread * Math.PI / 180.0;

            for (int w = 0; w < waves; w++)
            {
                // Pick an integer wave vector near the wind direction.
                for (int attempt = 0; ; attempt++)
                {
                    double k = minK + random.NextDouble() * (maxK - minK);
                    double dir = wind + (random.NextDouble() * 2.0 - 1.0) * spreadRad;
                    kx[w] = (int)Math.Round(Math.Cos(dir) * k);
                    ky[w] = (int)Math.Round(Math.Sin(dir) * k);
                    if (kx[w] != 0 || ky[w] != 0 || attempt > 20) break;
                }

                double kMag = Math.Sqrt(kx[w] * kx[w] + ky[w] * ky[w]);
                amp[w] = 1.0 / Math.Pow(Math.Max(kMag, 1.0), 1.6);
                phase[w] = random.NextDouble() * Math.PI * 2.0;
            }

            // Normalise so the steepest combined slope lands near `slope`.
            double slopeSum = 0.0;
            for (int w = 0; w < waves; w++)
                slopeSum += amp[w] * 2.0 * Math.PI * Math.Sqrt(kx[w] * kx[w] + ky[w] * ky[w]);
            double scale = slope / Math.Max(slopeSum * 0.35, 1e-9);

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double u = x / (double)size, v = y / (double)size;
                    double dhdx = 0.0, dhdy = 0.0;

                    for (int w = 0; w < waves; w++)
                    {
                        double arg = 2.0 * Math.PI * (kx[w] * u + ky[w] * v) + phase[w];
                        double s = -Math.Sin(arg) * amp[w] * 2.0 * Math.PI;
                        dhdx += s * kx[w];
                        dhdy += s * ky[w];
                    }

                    var n = new Vector3((float)(-dhdx * scale), (float)(-dhdy * scale), 1f).normalized;
                    pixels[y * size + x] = new Color32((byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f),
                                                       (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f),
                                                       (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f),
                                                       255);
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        static void ConfigureNormalMap(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;

            importer.textureType = TextureImporterType.NormalMap;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 8;
            importer.filterMode = FilterMode.Trilinear;
            importer.SaveAndReimport();
        }

        /// Import settings for the equirectangular world image used by the debug mini-map.
        [MenuItem("CirrusFS/Ocean/Configure World Map Import")]
        public static void ConfigureWorldMap()
        {
            const string path = "Assets/Textures/World/BlueMarble_5400x2700.jpg";
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;

            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 8192;
            importer.mipmapEnabled = true;
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
    }
}
