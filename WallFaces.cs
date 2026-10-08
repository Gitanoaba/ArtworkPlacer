using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // ─── GEOMETRÍA COMÚN DE MUROS ────────────────────────────────────────────
    // Caras de un muro, obstáculos, tramos libres y creación de instancias.
    // La usan Place Artwork (WallPlacer) y Place TV (TvPlacer).
    internal abstract class WallFaces
    {
        protected const double Eps = 0.01;               // pies

        protected readonly Document _doc;
        protected readonly List<FamilySymbol> _symbols;
        protected readonly double _clearance;
        protected readonly Phase _phase;
        protected readonly Dictionary<ElementId, double> _widths = new Dictionary<ElementId, double>();   // ancho real de cada tipo (-1 = no se pudo colocar)

        private readonly string _marker;                  // se escribe en Comments de cada pieza colocada
        private readonly HashSet<ElementId> _ownFamilyIds;
        private readonly Dictionary<ElementId, List<FamilyInstance>> _hostedByWall;
        private readonly List<FamilyInstance> _looseOwn;  // piezas nuestras sin muro host (familias sueltas)

        protected XYZ _p0, _d, _n;

        protected WallFaces(Document doc, List<FamilySymbol> symbols, double clearance, string marker)
        {
            _doc = doc;
            _symbols = symbols;
            _clearance = clearance;
            _marker = marker;
            _ownFamilyIds = new HashSet<ElementId>(symbols.Select(s => s.Family.Id));

            ElementId phaseId = doc.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            _phase = phaseId != null ? doc.GetElement(phaseId) as Phase : null;

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .ToList();
            _hostedByWall = instances
                .Where(fi => fi.Host is Wall)
                .GroupBy(fi => fi.Host.Id)
                .ToDictionary(g => g.Key, g => g.ToList());
            _looseOwn = instances.Where(fi => !(fi.Host is Wall) && IsOwn(fi)).ToList();
        }

        private bool IsOwn(FamilyInstance fi) =>
            _ownFamilyIds.Contains(fi.Symbol.Family.Id) ||
            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == _marker;

        protected void Mark(FamilyInstance fi) =>
            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(_marker);

        // Geometría de una cara del muro: +1 = exterior, -1 = interior
        protected class Side
        {
            public int Sign;
            public double V;                                   // posición de la cara sobre la normal
            public List<Reference> Refs = new List<Reference>();
            public List<PlanarFace> Faces = new List<PlanarFace>();
            public List<Tuple<double, double>> Blocked = new List<Tuple<double, double>>();
            public List<Tuple<double, double>> Existing = new List<Tuple<double, double>>();   // tramos con piezas nuestras ya colocadas
            public bool HasExisting => Existing.Count > 0;
            public string Name => Sign > 0 ? "exterior face" : "interior face";
        }

        // Caja del elemento en coordenadas locales del muro: U a lo largo, V normal, Z vertical
        protected class Box { public double U0, U1, V0, V1, Z0, Z1; }

        // Resultado del análisis de un muro (deja _p0/_d/_n listos para LocalBox)
        protected class WallGeom
        {
            public Level Level;
            public double Length;
            public double RefZ;                                // altura de referencia para obstáculos y flechas
            public Dictionary<int, Side> Sides = new Dictionary<int, Side>();
        }

        // refHeight: altura sobre el nivel donde se buscan obstáculos y se dibujan las flechas
        protected WallGeom Analyze(Wall wall, double refHeight, out string error)
        {
            error = null;
            if (wall.WallType.Kind != WallKind.Basic) { error = "curtain/stacked walls are not supported"; return null; }
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) { error = "curved walls are not supported"; return null; }
            if (!(_doc.GetElement(wall.LevelId) is Level level)) { error = "wall has no level"; return null; }

            _p0 = line.GetEndPoint(0);
            _d = line.Direction;
            _n = wall.Orientation;                 // apunta hacia la cara exterior

            var g = new WallGeom
            {
                Level = level,
                Length = line.Length,
                RefZ = level.ProjectElevation + refHeight
            };
            AddSide(wall, ShellLayerType.Exterior, +1, g.Sides);
            AddSide(wall, ShellLayerType.Interior, -1, g.Sides);
            if (g.Sides.Count == 0) { error = "no planar faces found"; return null; }

            CollectObstacles(wall, g.Sides, g.RefZ, g.Length);
            return g;
        }

        private void AddSide(Wall wall, ShellLayerType shell, int sign, Dictionary<int, Side> sides)
        {
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetSideFaces(wall, shell); }
            catch { return; }

            var side = new Side { Sign = sign };
            foreach (Reference r in refs)
            {
                if (wall.GetGeometryObjectFromReference(r) is PlanarFace f)
                {
                    side.Refs.Add(r);
                    side.Faces.Add(f);
                }
            }
            if (side.Faces.Count == 0) return;
            side.V = (side.Faces[0].Origin - _p0).DotProduct(_n);
            sides[sign] = side;
        }

        // Puertas, ventanas, aberturas, cosas ya colgadas en el muro y muros que lo tocan
        private void CollectObstacles(Wall wall, Dictionary<int, Side> sides, double refZ, double length)
        {
            var insertIds = new HashSet<ElementId>(wall.FindInserts(true, false, true, true));

            foreach (ElementId id in insertIds)
            {
                Box b = LocalBox(_doc.GetElement(id));
                if (b == null) continue;
                foreach (Side s in sides.Values) s.Blocked.Add(Tuple.Create(b.U0, b.U1));
            }

            if (_hostedByWall.TryGetValue(wall.Id, out var hosted))
            {
                foreach (FamilyInstance fi in hosted)
                {
                    if (insertIds.Contains(fi.Id)) continue;
                    Box b = LocalBox(fi);
                    if (b == null) continue;
                    bool own = IsOwn(fi);
                    foreach (Side s in AffectedSides(b, sides, true))
                    {
                        s.Blocked.Add(Tuple.Create(b.U0, b.U1));
                        if (own) s.Existing.Add(Tuple.Create(b.U0, b.U1));
                    }
                }
            }

            BoundingBoxXYZ wb = wall.get_BoundingBox(null);
            if (wb == null) return;

            // Piezas nuestras sueltas (sin host) apoyadas contra una cara de este muro
            foreach (FamilyInstance fi in _looseOwn)
            {
                Box b = LocalBox(fi);
                if (b == null || b.U1 < 0 || b.U0 > length || b.Z1 < wb.Min.Z || b.Z0 > wb.Max.Z) continue;
                foreach (Side s in sides.Values)
                {
                    bool touches = s.Sign > 0
                        ? b.V1 > s.V + Eps && b.V0 < s.V + 0.5
                        : b.V0 < s.V - Eps && b.V1 > s.V - 0.5;
                    if (!touches) continue;
                    s.Blocked.Add(Tuple.Create(b.U0, b.U1));
                    s.Existing.Add(Tuple.Create(b.U0, b.U1));
                }
            }

            var pad = new XYZ(0.1, 0.1, 0.1);
            var others = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(wb.Min - pad, wb.Max + pad)))
                .Where(e => e.Id != wall.Id && !insertIds.Contains(e.Id));

            foreach (Element other in others)
            {
                Box b = LocalBox(other);
                if (b == null || b.Z0 > refZ || b.Z1 < refZ) continue;
                foreach (Side s in AffectedSides(b, sides, false))
                    s.Blocked.Add(Tuple.Create(b.U0, b.U1));
            }
        }

        // Una caja bloquea una cara si sobresale de ella; si queda dentro del muro, opcionalmente bloquea ambas
        private static IEnumerable<Side> AffectedSides(Box b, Dictionary<int, Side> sides, bool insideBlocksBoth)
        {
            var hit = new List<Side>();
            if (sides.TryGetValue(+1, out Side ext) && b.V1 > ext.V + Eps) hit.Add(ext);
            if (sides.TryGetValue(-1, out Side inn) && b.V0 < inn.V - Eps) hit.Add(inn);
            if (hit.Count == 0 && insideBlocksBoth) hit.AddRange(sides.Values);
            return hit;
        }

        // La cara que mira hacia un room; si hay rooms en ambos lados, el más grande. null = sin rooms
        protected Side RoomSide(WallGeom g)
        {
            XYZ mid = _p0 + _d * (g.Length / 2);
            double z = g.Level.ProjectElevation + 3.0;
            Side best = null;
            double bestArea = -1;
            foreach (Side s in g.Sides.Values)
            {
                XYZ probe = mid + _n * (s.V + s.Sign * 1.0);
                probe = new XYZ(probe.X, probe.Y, z);
                Room room = _phase != null ? _doc.GetRoomAtPoint(probe, _phase) : _doc.GetRoomAtPoint(probe);
                if (room != null && room.Area > bestArea) { best = s; bestArea = room.Area; }
            }
            return best;
        }

        // Tramos libres de la cara entre from y to, del más largo al más corto
        protected List<Tuple<double, double>> FreeSegments(double from, double to, List<Tuple<double, double>> blocked)
        {
            var intervals = blocked
                .Select(b => Tuple.Create(Math.Max(from, b.Item1 - _clearance), Math.Min(to, b.Item2 + _clearance)))
                .Where(b => b.Item2 > b.Item1)
                .OrderBy(b => b.Item1);

            var free = new List<Tuple<double, double>>();
            double cursor = from + _clearance;
            foreach (var iv in intervals)
            {
                if (iv.Item1 - cursor > Eps) free.Add(Tuple.Create(cursor, iv.Item1));
                cursor = Math.Max(cursor, iv.Item2);
            }
            if (to - _clearance - cursor > Eps) free.Add(Tuple.Create(cursor, to - _clearance));
            return free.OrderByDescending(f => f.Item2 - f.Item1).ToList();
        }

        protected XYZ PointOnFace(Level level, Side side, double u, double heightAboveLevel)
        {
            XYZ onFace = _p0 + _d * u + _n * side.V;
            return new XYZ(onFace.X, onFace.Y, level.ProjectElevation + heightAboveLevel);
        }

        // Coloca el tipo, lee su caja y lo borra: devuelve el ancho a lo largo del muro (-1 = no se pudo)
        protected double Measure(FamilySymbol sym, Wall wall, WallGeom g, Side side, double u, double heightAboveLevel)
        {
            try
            {
                FamilyInstance fi = Create(sym, wall, g.Level, side, PointOnFace(g.Level, side, u, heightAboveLevel), _n * side.Sign);
                if (fi == null) return -1;
                _doc.Regenerate();
                Box box = LocalBox(fi);
                _doc.Delete(fi.Id);
                return box == null ? -1 : box.U1 - box.U0;
            }
            catch
            {
                return -1;
            }
        }

        protected FamilyInstance Create(FamilySymbol sym, Wall wall, Level level, Side side, XYZ pt, XYZ faceN)
        {
            switch (sym.Family.FamilyPlacementType)
            {
                case FamilyPlacementType.WorkPlaneBased:
                {
                    // Elegir la cara que contiene el punto (un muro puede tener la cara partida)
                    Reference faceRef = side.Refs[0];
                    for (int i = 0; i < side.Faces.Count; i++)
                        if (side.Faces[i].Project(pt) != null) { faceRef = side.Refs[i]; break; }

                    // X de la familia horizontal, Y hacia arriba
                    XYZ refDir = XYZ.BasisZ.CrossProduct(faceN);
                    return _doc.Create.NewFamilyInstance(faceRef, pt, refDir, sym);
                }

                case FamilyPlacementType.OneLevelBasedHosted:
                {
                    FamilyInstance fi = _doc.Create.NewFamilyInstance(pt, sym, wall, level, StructuralType.NonStructural);
                    _doc.Regenerate();
                    if (fi.FacingOrientation.DotProduct(faceN) < 0 && fi.CanFlipFacing) fi.flipFacing();
                    return fi;
                }

                case FamilyPlacementType.OneLevelBased:
                {
                    XYZ basePt = new XYZ(pt.X, pt.Y, level.ProjectElevation);
                    FamilyInstance fi = _doc.Create.NewFamilyInstance(basePt, sym, level, StructuralType.NonStructural);
                    fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)?.Set(pt.Z - level.ProjectElevation);
                    _doc.Regenerate();
                    double angle = fi.FacingOrientation.AngleOnPlaneTo(faceN, XYZ.BasisZ);
                    if (Math.Abs(angle) > 1e-6)
                        ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(basePt, basePt + XYZ.BasisZ), angle);
                    return fi;
                }
            }
            return null;
        }

        // "Default Elevation" del tipo, si existe
        protected static double DefaultElevation(FamilySymbol sym)
        {
            Parameter p = sym.LookupParameter("Default Elevation");
            if (p != null && p.StorageType == StorageType.Double) return p.AsDouble();
            return UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Centimeters);
        }

        protected Box LocalBox(Element e)
        {
            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            if (bb == null) return null;

            var box = new Box
            {
                U0 = double.MaxValue, U1 = double.MinValue,
                V0 = double.MaxValue, V1 = double.MinValue,
                Z0 = double.MaxValue, Z1 = double.MinValue
            };
            for (int i = 0; i < 8; i++)
            {
                XYZ c = new XYZ(
                    (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                c = bb.Transform.OfPoint(c);
                XYZ q = c - _p0;
                double u = q.DotProduct(_d), v = q.DotProduct(_n);
                box.U0 = Math.Min(box.U0, u); box.U1 = Math.Max(box.U1, u);
                box.V0 = Math.Min(box.V0, v); box.V1 = Math.Max(box.V1, v);
                box.Z0 = Math.Min(box.Z0, c.Z); box.Z1 = Math.Max(box.Z1, c.Z);
            }
            return box;
        }
    }
}
