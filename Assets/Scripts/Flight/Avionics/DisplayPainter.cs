using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Flight.Avionics
{
    public enum TextAlign { Left, Center, Right }

    /// Immediate-mode 2D painter for glass-cockpit displays. Draws filled polygons, thick lines
    /// and text into a RenderTexture through GL, in a y-down design space of the caller's
    /// choosing (a display is laid out in its reference artwork's pixels).
    ///
    /// Everything can be clipped to a convex region: a tape window, or the attitude sphere.
    /// Clipping is done on the CPU per primitive, so rotated geometry and partly hidden glyphs
    /// clip exactly with no stencil. Colours are given in sRGB, as picked from artwork.
    public sealed class DisplayPainter : IDisposable
    {
        const int GlyphPx = 64;
        const int MaxVerts = 512;
        const string Charset = "0123456789-+.:/ ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        readonly Material solidMat, textMat;
        readonly bool linear;
        Font font;
        FontStyle style;
        float digitCap;

        Material bound;
        RenderTexture prevActive;

        // Clip half-planes (nx, ny, d): inside when nx*x + ny*y >= d.
        readonly List<Vector3> planes = new List<Vector3>();
        readonly Stack<int> clipStack = new Stack<int>();

        Vector2[] pa = new Vector2[MaxVerts], pb = new Vector2[MaxVerts];
        Vector2[] ua = new Vector2[MaxVerts], ub = new Vector2[MaxVerts];

        /// Horizontal squeeze applied to all text; avionics fonts are narrow.
        public float textWidthScale = 0.85f;

        public DisplayPainter(Shader shader, Font font, FontStyle style)
        {
            solidMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            solidMat.mainTexture = Texture2D.whiteTexture;
            textMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            this.font = font;
            this.style = style;
        }

        public void Dispose()
        {
            Destroy(solidMat);
            Destroy(textMat);
        }

        static void Destroy(UnityEngine.Object o)
        {
            if (!o) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- frame -------------------------------------------------------------

        /// Starts drawing into `target`; `design` is the region of design space that fills it.
        public void Begin(RenderTexture target, Rect design, Color clear)
        {
            prevActive = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(design.xMin, design.xMax, design.yMax, design.yMin);
            GL.Clear(true, true, linear ? clear.linear : clear);

            if (font)
            {
                font.RequestCharactersInTexture(Charset, GlyphPx, style);
                textMat.mainTexture = font.material ? font.material.mainTexture : null;
                digitCap = font.GetCharacterInfo('0', out CharacterInfo ci, GlyphPx, style) && ci.maxY > 0
                    ? ci.maxY
                    : GlyphPx * 0.7f;
            }
            bound = null;
            planes.Clear();
            clipStack.Clear();
        }

        public void End()
        {
            if (bound) GL.End();
            bound = null;
            GL.PopMatrix();
            RenderTexture.active = prevActive;
        }

        void Bind(Material m)
        {
            if (bound == m) return;
            if (bound) GL.End();
            m.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            bound = m;
        }

        // ---- clipping ----------------------------------------------------------

        public void PushClipRect(float x0, float y0, float x1, float y1)
        {
            clipStack.Push(planes.Count);
            planes.Add(new Vector3(1f, 0f, x0));
            planes.Add(new Vector3(-1f, 0f, -x1));
            planes.Add(new Vector3(0f, 1f, y0));
            planes.Add(new Vector3(0f, -1f, -y1));
        }

        /// Any convex polygon, either winding.
        public void PushClipPolygon(IList<Vector2> pts)
        {
            clipStack.Push(planes.Count);
            Vector2 c = Vector2.zero;
            foreach (var p in pts) c += p;
            c /= pts.Count;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
                Vector2 e = b - a;
                if (e.sqrMagnitude < 1e-8f) continue;
                var n = new Vector2(-e.y, e.x).normalized;
                if (Vector2.Dot(n, c - a) < 0f) n = -n;
                planes.Add(new Vector3(n.x, n.y, Vector2.Dot(n, a)));
            }
        }

        public void PopClip()
        {
            if (clipStack.Count == 0) return;
            int keep = clipStack.Pop();
            planes.RemoveRange(keep, planes.Count - keep);
        }

        /// Sutherland-Hodgman against every active plane, on pa/ua in place.
        int Clip(int n)
        {
            for (int k = 0; k < planes.Count && n >= 3; k++)
            {
                Vector3 pl = planes[k];
                int m = 0;
                for (int i = 0; i < n; i++)
                {
                    int j = i + 1 == n ? 0 : i + 1;
                    Vector2 a = pa[i], b = pa[j];
                    float da = pl.x * a.x + pl.y * a.y - pl.z;
                    float db = pl.x * b.x + pl.y * b.y - pl.z;
                    if (da >= 0f && m < MaxVerts)
                    {
                        pb[m] = a;
                        ub[m] = ua[i];
                        m++;
                    }
                    if ((da >= 0f) != (db >= 0f) && m < MaxVerts)
                    {
                        float t = da / (da - db);
                        pb[m] = Vector2.LerpUnclamped(a, b, t);
                        ub[m] = Vector2.LerpUnclamped(ua[i], ua[j], t);
                        m++;
                    }
                }
                (pa, pb) = (pb, pa);
                (ua, ub) = (ub, ua);
                n = m;
            }
            return n;
        }

        /// Emits the convex polygon currently in pa/ua.
        void Emit(Material m, int n, Color c)
        {
            n = Clip(n);
            if (n < 3) return;
            Bind(m);
            GL.Color(linear ? c.linear : c);
            for (int i = 1; i < n - 1; i++)
            {
                V(0);
                V(i);
                V(i + 1);
            }
        }

        void V(int i)
        {
            GL.TexCoord2(ua[i].x, ua[i].y);
            GL.Vertex3(pa[i].x, pa[i].y, 0f);
        }

        // ---- shapes ------------------------------------------------------------

        public void FillRect(float x0, float y0, float x1, float y1, Color c)
        {
            pa[0] = new Vector2(x0, y0);
            pa[1] = new Vector2(x1, y0);
            pa[2] = new Vector2(x1, y1);
            pa[3] = new Vector2(x0, y1);
            for (int i = 0; i < 4; i++) ua[i] = Vector2.zero;
            Emit(solidMat, 4, c);
        }

        public void Triangle(Vector2 a, Vector2 b, Vector2 d, Color c)
        {
            pa[0] = a;
            pa[1] = b;
            pa[2] = d;
            for (int i = 0; i < 3; i++) ua[i] = Vector2.zero;
            Emit(solidMat, 3, c);
        }

        public void Quad(Vector2 a, Vector2 b, Vector2 d, Vector2 e, Color c)
        {
            pa[0] = a;
            pa[1] = b;
            pa[2] = d;
            pa[3] = e;
            for (int i = 0; i < 4; i++) ua[i] = Vector2.zero;
            Emit(solidMat, 4, c);
        }

        /// Filled convex polygon.
        public void FillPolygon(IList<Vector2> pts, Color c)
        {
            int n = Mathf.Min(pts.Count, MaxVerts / 2);
            for (int i = 0; i < n; i++)
            {
                pa[i] = pts[i];
                ua[i] = Vector2.zero;
            }
            Emit(solidMat, n, c);
        }

        /// Thick line. `cap` extends both ends by half the width, which closes polyline corners.
        public void Line(Vector2 a, Vector2 b, float width, Color c, bool cap = false)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            d /= len;
            if (cap)
            {
                a -= d * (width * 0.5f);
                b += d * (width * 0.5f);
            }
            var n = new Vector2(-d.y, d.x) * (width * 0.5f);
            Quad(a + n, b + n, b - n, a - n, c);
        }

        public void Polyline(IList<Vector2> pts, float width, Color c, bool closed)
        {
            int n = pts.Count;
            for (int i = 0; i < n - 1; i++) Line(pts[i], pts[i + 1], width, c, true);
            if (closed && n > 2) Line(pts[n - 1], pts[0], width, c, true);
        }

        public void Polyline(float width, Color c, bool closed, params Vector2[] pts) =>
            Polyline((IList<Vector2>)pts, width, c, closed);

        public void OutlineRect(float x0, float y0, float x1, float y1, float width, Color c)
        {
            Line(new Vector2(x0, y0), new Vector2(x1, y0), width, c, true);
            Line(new Vector2(x1, y0), new Vector2(x1, y1), width, c, true);
            Line(new Vector2(x1, y1), new Vector2(x0, y1), width, c, true);
            Line(new Vector2(x0, y1), new Vector2(x0, y0), width, c, true);
        }

        // ---- text --------------------------------------------------------------

        /// Width of `s` at digit height `height`.
        public float MeasureText(string s, float height)
        {
            if (string.IsNullOrEmpty(s) || !font) return 0f;
            font.RequestCharactersInTexture(s, GlyphPx, style);
            float sx = height / digitCap * textWidthScale;
            float w = 0f;
            foreach (char ch in s)
                if (font.GetCharacterInfo(ch, out CharacterInfo ci, GlyphPx, style))
                    w += ci.advance * sx;
            return w;
        }

        /// Text whose digits are `height` tall, vertically centred on y. `rotationDeg` turns it
        /// about (x, y), positive = counter-clockwise on screen. Returns the drawn width.
        public float Text(string s, float x, float y, float height, Color c,
                          TextAlign align = TextAlign.Left, float rotationDeg = 0f)
        {
            if (string.IsNullOrEmpty(s) || !font) return 0f;
            float w = MeasureText(s, height);
            float sy = height / digitCap, sx = sy * textWidthScale;
            float pen = align == TextAlign.Left ? 0f : align == TextAlign.Center ? -w * 0.5f : -w;
            float baseY = height * 0.5f;

            float rad = rotationDeg * Mathf.Deg2Rad;
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            bool rotated = Mathf.Abs(rotationDeg) > 1e-3f;
            var origin = new Vector2(x, y);

            foreach (char ch in s)
            {
                if (!font.GetCharacterInfo(ch, out CharacterInfo ci, GlyphPx, style)) continue;
                if (ci.maxX > ci.minX && ci.maxY > ci.minY)
                {
                    float x0 = pen + ci.minX * sx, x1 = pen + ci.maxX * sx;
                    float y0 = baseY - ci.maxY * sy, y1 = baseY - ci.minY * sy;
                    pa[0] = new Vector2(x0, y0);
                    pa[1] = new Vector2(x1, y0);
                    pa[2] = new Vector2(x1, y1);
                    pa[3] = new Vector2(x0, y1);
                    ua[0] = ci.uvTopLeft;
                    ua[1] = ci.uvTopRight;
                    ua[2] = ci.uvBottomRight;
                    ua[3] = ci.uvBottomLeft;
                    for (int i = 0; i < 4; i++)
                    {
                        Vector2 p = pa[i];
                        pa[i] = rotated
                            ? origin + new Vector2(p.x * cs + p.y * sn, -p.x * sn + p.y * cs)
                            : origin + p;
                    }
                    Emit(textMat, 4, c);
                }
                pen += ci.advance * sx;
            }
            return w;
        }
    }
}
