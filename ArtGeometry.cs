using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── GEOMETRÍA VISIBLE DE UNA PIEZA ──────────────────────────────────────
    // La caja de Revit de una familia incluye líneas, objetos ocultos y cualquier cosa que el
    // modelador haya dejado (p.ej. referencias de "height from the floor"), así que su centro no es
    // el centro del cuadro. Aquí solo cuentan los sólidos y mallas visibles.
    internal static class ArtGeometry
    {
        private static readonly Options GeomOptions = new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = false
        };

        public static List<XYZ> VisiblePoints(Element e)
        {
            var pts = new List<XYZ>();
            GeometryElement ge = e?.get_Geometry(GeomOptions);
            if (ge != null) Collect(ge, Transform.Identity, pts);
            return pts;
        }

        // Se recorre la geometría del símbolo (alineada con la familia) y se transforma,
        // así la caja no crece en muros diagonales
        private static void Collect(GeometryElement ge, Transform t, List<XYZ> pts)
        {
            foreach (GeometryObject go in ge)
            {
                switch (go)
                {
                    case GeometryInstance gi:
                        Collect(gi.GetSymbolGeometry(), t.Multiply(gi.Transform), pts);
                        break;

                    case Solid s when s.Faces.Size > 0 && (s.Volume > 1e-9 || s.SurfaceArea > 1e-9):
                        BoundingBoxXYZ bb = s.GetBoundingBox();
                        for (int i = 0; i < 8; i++)
                        {
                            XYZ c = new XYZ(
                                (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                            pts.Add(t.OfPoint(bb.Transform.OfPoint(c)));
                        }
                        break;

                    case Mesh m:
                        foreach (XYZ v in m.Vertices) pts.Add(t.OfPoint(v));
                        break;
                }
            }
        }

        // Altura mínima y máxima de lo visible; si no hay sólidos, la caja del elemento
        public static bool ZRange(Element e, out double z0, out double z1)
        {
            List<XYZ> pts = VisiblePoints(e);
            if (pts.Count > 0)
            {
                z0 = pts.Min(p => p.Z);
                z1 = pts.Max(p => p.Z);
                return true;
            }

            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            z0 = bb?.Min.Z ?? 0;
            z1 = bb?.Max.Z ?? 0;
            return bb != null;
        }

        // Cuánto subir/bajar la pieza para que su centro, borde inferior o superior quede en targetZ
        public static double AlignDelta(HeightAlign align, double targetZ, double z0, double z1)
        {
            switch (align)
            {
                case HeightAlign.Bottom: return targetZ - z0;
                case HeightAlign.Top: return targetZ - z1;
                default: return targetZ - (z0 + z1) / 2;
            }
        }

        // Nivel desde el que se mide la altura: el del muro host (igual que al colocar), si no el de la pieza
        public static Level ReferenceLevel(Document doc, FamilyInstance fi)
        {
            if (fi.Host is Wall w && doc.GetElement(w.LevelId) is Level wallLevel) return wallLevel;
            if (doc.GetElement(fi.LevelId) is Level own) return own;
            ElementId schedule = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsElementId();
            return schedule != null ? doc.GetElement(schedule) as Level : null;
        }
    }
}
