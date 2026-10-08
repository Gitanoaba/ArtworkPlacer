using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // Un lugar posible para la TV: una cara de un muro, limitada al tramo que da a la sala
    internal class TvSpot
    {
        public Wall Wall;
        public int Sign;                 // +1 exterior, -1 interior
        public double U0, U1;            // tramo sobre la línea del muro
        public double? Depth;            // profundidad de la sala frente a esta cara (null = desconocida)

        // Lo completa TvPlacer.Evaluate
        public bool Usable;              // muro recto básico con esa cara
        public bool HasTv;               // ya tiene una TV en ese tramo
        public FamilySymbol Symbol;      // TV elegida
        public double Center;            // posición sobre el muro
        public string Problem;           // null = se puede colocar
        public double FaceV;             // posición de la cara sobre la normal del muro
        public XYZ OnFace, Normal, Along;
    }

    // Una TV a colocar: una sala (varios muros posibles) o un muro suelto (sus dos caras)
    internal class TvTarget
    {
        public string Label;
        public List<TvSpot> Spots = new List<TvSpot>();   // ordenados del mejor al peor
        public int Current = -1;                          // -1 = sin TV
        public int LastOn;
        public string Note;                               // por qué no lleva TV, si no lleva
        public TvSpot Chosen => Current >= 0 && Current < Spots.Count ? Spots[Current] : null;

        public void Toggle()
        {
            if (Current >= 0) { LastOn = Current; Current = -1; Note = Note ?? "switched off in the preview"; }
            else if (Spots.Count > 0) Current = Math.Min(LastOn, Spots.Count - 1);
        }
    }

    // ─── LÓGICA DE COLOCACIÓN DE TVs ─────────────────────────────────────────
    internal class TvPlacer : WallFaces
    {
        private const string Marker = "TvPlacer";       // se escribe en Comments de cada TV colocada
        private const double DoorReach = 1.0;           // pies: una puerta a menos de esto de un muro está "en" ese muro

        private readonly double? _bottom;               // borde inferior sobre el nivel; null = altura de la familia
        private readonly bool _autoSize;
        private readonly double _depthFactor;           // una TV sirve salas de hasta ancho × factor de profundidad
        private readonly Func<double, string> _fmt;
        private readonly View3D _view3D;                // para medir la profundidad frente a un muro suelto

        public TvPlacer(Document doc, List<FamilySymbol> symbols, double clearance, double? bottom, bool autoSize, double depthFactor, Func<double, string> fmt)
            : base(doc, symbols, clearance, Marker)
        {
            _bottom = bottom;
            _autoSize = autoSize;
            _depthFactor = depthFactor;
            _fmt = fmt;
            _view3D = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate);
        }

        private static double Cm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Centimeters);

        // Altura de referencia para obstáculos y flechas: más o menos el medio de la TV
        private double RefHeight => (_bottom ?? Cm(100)) + Cm(50);

        private double Height(FamilySymbol sym) => _bottom ?? DefaultElevation(sym);

        private double WidthOf(FamilySymbol s) => _widths.TryGetValue(s.Id, out double w) ? w : -1;

        public List<FamilySymbol> Unmeasured => _symbols.Where(s => WidthOf(s) <= 0).ToList();

        // Mide el ancho real de cada tipo (lo coloca en algún muro y lo borra).
        // Va dentro de una transacción que después se deshace.
        public void MeasureTypes(IEnumerable<Wall> walls)
        {
            foreach (Wall wall in walls)
            {
                if (Unmeasured.Count == 0) return;
                WallGeom g = Analyze(wall, RefHeight, out _);
                if (g == null) continue;
                Side side = g.Sides.Values.First();
                foreach (FamilySymbol sym in Unmeasured)
                    _widths[sym.Id] = Measure(sym, wall, g, side, g.Length / 2, Height(sym));
            }
        }

        // ── Armado de objetivos ──

        // Sala: se prueban todas sus caras de muro. Orden: primero las que tienen lugar, luego los muros
        // cortos (en la punta de la sala, mirando a lo largo de la mesa), sin puerta y lo más lejos posible de ella.
        public TvTarget RoomTarget(FoundRoom room, int index, List<Outline> doors)
        {
            var target = new TvTarget { Label = RoomLabel(room, index) };

            double z = (_doc.GetElement(room.LevelId) as Level)?.ProjectElevation ?? 0;
            List<XYZ> roomDoors = doors
                .Where(o => o.MinimumPoint.Z <= z + 3 && o.MaximumPoint.Z >= z + 3)
                .Select(o => RoomFinder.Flat((o.MinimumPoint + o.MaximumPoint) / 2))
                .Where(p => DistanceToPolygon(p, room.Polygon) < DoorReach)
                .ToList();

            var ranked = new List<Tuple<TvSpot, bool, bool, double>>();   // lugar, muro corto, tiene puerta, distancia a la puerta
            foreach (RoomWall rw in room.Walls)
            {
                XYZ along = rw.B - rw.A;
                if (along.GetLength() < Eps) continue;
                along = along.Normalize();
                XYZ inward = RoomFinder.Flat(rw.Wall.Orientation).Normalize() * rw.Sign;
                double width = Extent(room.Polygon, along), depth = Extent(room.Polygon, inward);

                var spot = new TvSpot { Wall = rw.Wall, Sign = rw.Sign, U0 = rw.U0, U1 = rw.U1, Depth = depth };
                Evaluate(spot);
                if (!spot.Usable) continue;    // muros cortina, curvos…

                bool shortWall = depth >= width * 0.95;
                bool doorOn = roomDoors.Any(p => DistanceToSegment(p, rw.A, rw.B) < DoorReach);
                double doorDist = roomDoors.Count == 0 ? 0 : roomDoors.Min(p => p.DistanceTo(RoomFinder.Flat(spot.OnFace)));
                ranked.Add(Tuple.Create(spot, shortWall, doorOn, doorDist));
            }

            target.Spots = ranked
                .OrderByDescending(r => r.Item1.Problem == null)
                .ThenByDescending(r => r.Item2)
                .ThenBy(r => r.Item3)
                .ThenByDescending(r => r.Item4)
                .ThenByDescending(r => r.Item1.Depth)
                .Select(r => r.Item1)
                .ToList();

            if (target.Spots.Count == 0) target.Note = "no straight basic wall to hang a TV on";
            else if (target.Spots.Any(s => s.HasTv)) target.Note = "already has a TV";
            else target.Current = 0;
            return target;
        }

        // Muro suelto (no cierra una sala con los demás): la TV va en una de sus caras,
        // primero la que mira a un room de Revit si hay, si no la interior
        public TvTarget WallTarget(Wall wall)
        {
            var target = new TvTarget { Label = PlaceArtworkCommand.WallLabel(wall) };
            WallGeom g = Analyze(wall, RefHeight, out string error);
            if (g == null) { target.Note = error; return target; }

            Side first = RoomSide(g) ?? (g.Sides.TryGetValue(-1, out Side inn) ? inn : g.Sides.Values.First());
            var sides = g.Sides.Values.OrderBy(s => s == first ? 0 : 1).ToList();
            var depths = sides.Select(s => RayDepth(g, s)).ToList();
            for (int i = 0; i < sides.Count; i++)
            {
                var spot = new TvSpot { Wall = wall, Sign = sides[i].Sign, U0 = 0, U1 = g.Length, Depth = depths[i] };
                Evaluate(spot);
                target.Spots.Add(spot);
            }

            if (target.Spots.Any(s => s.HasTv)) target.Note = "already has a TV";
            else target.Current = 0;
            return target;
        }

        // Nombre del room de Revit en el medio de la sala, si hay uno; si no, los muros
        private string RoomLabel(FoundRoom room, int index)
        {
            XYZ c = room.Polygon.Aggregate(XYZ.Zero, (acc, p) => acc + p) / room.Polygon.Count;
            if (_doc.GetElement(room.LevelId) is Level level)
            {
                XYZ probe = new XYZ(c.X, c.Y, level.ProjectElevation + 3);
                Room r = _phase != null ? _doc.GetRoomAtPoint(probe, _phase) : _doc.GetRoomAtPoint(probe);
                if (r != null) return $"Room {index} \"{r.Name}\"";
            }
            var ids = room.Walls.Select(w => w.Wall.Id.Value.ToString()).Take(4);
            return $"Room {index} (walls {string.Join(", ", ids)}{(room.Walls.Count > 4 ? "…" : "")})";
        }

        // Distancia desde la cara hasta el primer muro de enfrente (necesita una vista 3D)
        private double? RayDepth(WallGeom g, Side side)
        {
            if (_view3D == null) return null;
            XYZ dir = _n * side.Sign;
            XYZ origin = FacePoint(g, side, g.Length / 2) + dir * 0.05;
            try
            {
                var ri = new ReferenceIntersector(new ElementClassFilter(typeof(Wall)), FindReferenceTarget.Face, _view3D);
                return ri.FindNearest(origin, dir)?.Proximity;
            }
            catch
            {
                return null;
            }
        }

        // ── Evaluación y colocación ──

        // Decide qué TV va en ese lugar y dónde; deja el resultado en el spot
        public void Evaluate(TvSpot spot) => Evaluate(spot, out _);

        private Side Evaluate(TvSpot spot, out WallGeom g)
        {
            spot.Symbol = null;
            spot.Problem = null;
            spot.HasTv = false;

            g = Analyze(spot.Wall, RefHeight, out string error);
            Side side = null;
            spot.Usable = g != null && g.Sides.TryGetValue(spot.Sign, out side);
            if (!spot.Usable) { spot.Problem = error ?? "face not found"; return null; }

            double from = Math.Max(0, spot.U0), to = Math.Min(g.Length, spot.U1);
            double mid = (from + to) / 2;
            spot.FaceV = side.V;
            spot.Normal = _n * spot.Sign;
            spot.Along = _d;
            spot.Center = mid;
            spot.OnFace = FacePoint(g, side, mid);

            spot.HasTv = side.Existing.Any(e => e.Item2 > from && e.Item1 < to);
            if (spot.HasTv) { spot.Problem = "already has a TV"; return null; }

            List<Tuple<double, double>> free = FreeSegments(from, to, side.Blocked);
            var sized = _symbols.Where(s => WidthOf(s) > 0).OrderBy(WidthOf).ToList();
            if (sized.Count == 0) { spot.Problem = "none of the TV types could be placed"; return null; }

            // Tipos que caben, de menor a mayor, con su centro
            var fitting = new List<Tuple<FamilySymbol, double, double>>();
            foreach (FamilySymbol s in sized)
                if (TryCenter(free, WidthOf(s), mid, out double c)) fitting.Add(Tuple.Create(s, WidthOf(s), c));

            if (fitting.Count == 0)
            {
                double longest = free.Count > 0 ? free[0].Item2 - free[0].Item1 : 0;
                spot.Problem = $"free space ({_fmt(longest)}) is narrower than the smallest TV ({_fmt(WidthOf(sized[0]))})";
                return null;
            }

            // Auto: la más chica que alcanza para la profundidad de la sala (o la más grande que cabe).
            // Sin profundidad conocida, la más chica. Modo fijo: la más grande que cabe.
            var pick = fitting.Last();
            if (_autoSize)
                pick = spot.Depth.HasValue
                    ? fitting.FirstOrDefault(f => f.Item2 * _depthFactor >= spot.Depth.Value) ?? fitting.Last()
                    : fitting.First();

            spot.Symbol = pick.Item1;
            spot.Center = pick.Item3;
            spot.OnFace = FacePoint(g, side, pick.Item3);
            return side;
        }

        // Lo más cerca posible del medio del tramo (el eje de la sala), dentro de un tramo libre donde quepa
        private static bool TryCenter(List<Tuple<double, double>> free, double width, double mid, out double center)
        {
            center = 0;
            double best = double.MaxValue;
            foreach (var seg in free)
            {
                if (seg.Item2 - seg.Item1 + Eps < width) continue;
                double lo = seg.Item1 + width / 2, hi = seg.Item2 - width / 2;
                double c = lo > hi ? (seg.Item1 + seg.Item2) / 2 : Math.Min(Math.Max(mid, lo), hi);
                if (Math.Abs(c - mid) < best) { best = Math.Abs(c - mid); center = c; }
            }
            return best < double.MaxValue;
        }

        public FamilyInstance Place(TvSpot spot, out string reason)
        {
            // Se vuelve a evaluar por si otra TV de esta corrida ocupó el lugar
            Side side = Evaluate(spot, out WallGeom g);
            if (side == null) { reason = spot.Problem; return null; }

            FamilyInstance fi = Create(spot.Symbol, spot.Wall, g.Level, side,
                PointOnFace(g.Level, side, spot.Center, Height(spot.Symbol)), _n * side.Sign);
            if (fi == null) { reason = "the TV could not be placed on this face"; return null; }
            _doc.Regenerate();

            Box box = LocalBox(fi);
            if (box != null)
            {
                XYZ move = _d * (spot.Center - (box.U0 + box.U1) / 2);
                if (_bottom.HasValue)
                    move += XYZ.BasisZ * (g.Level.ProjectElevation + _bottom.Value - box.Z0);
                if (!move.IsZeroLength())
                    ElementTransformUtils.MoveElement(_doc, fi.Id, move);
            }

            Mark(fi);
            reason = null;
            return fi;
        }

        // ── Geometría en planta ──

        private XYZ FacePoint(WallGeom g, Side side, double u)
        {
            XYZ p = _p0 + _d * u + _n * side.V;
            return new XYZ(p.X, p.Y, g.RefZ);
        }

        private static double Extent(List<XYZ> poly, XYZ dir)
        {
            var proj = poly.Select(p => p.DotProduct(dir)).ToList();
            return proj.Max() - proj.Min();
        }

        private static double DistanceToSegment(XYZ p, XYZ a, XYZ b)
        {
            XYZ ab = b - a;
            double len2 = ab.DotProduct(ab);
            double t = len2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (p - a).DotProduct(ab) / len2));
            return p.DistanceTo(a + ab * t);
        }

        private static double DistanceToPolygon(XYZ p, List<XYZ> poly) =>
            Enumerable.Range(0, poly.Count).Min(i => DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Count]));
    }
}
