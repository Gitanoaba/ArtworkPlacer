using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace ArtworkPlacer;

internal class TvPlacer : WallFaces
{
	private const string Marker = "TvPlacer";

	private const double DoorReach = 1.0;

	private readonly double? _bottom;

	private readonly bool _autoSize;

	private readonly double _depthFactor;

	private readonly Func<double, string> _fmt;

	private readonly View3D _view3D;

	private double RefHeight => (_bottom ?? Cm(100.0)) + Cm(50.0);

	public List<FamilySymbol> Unmeasured => _symbols.Where((FamilySymbol s) => WidthOf(s) <= 0.0).ToList();

	public TvPlacer(Document doc, List<FamilySymbol> symbols, double clearance, double? bottom, bool autoSize, double depthFactor, Func<double, string> fmt)
		: base(doc, symbols, clearance, "TvPlacer")
	{
		_bottom = bottom;
		_autoSize = autoSize;
		_depthFactor = depthFactor;
		_fmt = fmt;
		_view3D = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault((View3D v) => !v.IsTemplate);
	}

	private static double Cm(double v)
	{
		return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Centimeters);
	}

	private double Height(FamilySymbol sym)
	{
		return _bottom ?? WallFaces.DefaultElevation(sym);
	}

	private double WidthOf(FamilySymbol s)
	{
		if (!_widths.TryGetValue(s.Id, out var value))
		{
			return -1.0;
		}
		return value;
	}

	public void MeasureTypes(IEnumerable<Wall> walls)
	{
		foreach (Wall wall in walls)
		{
			if (Unmeasured.Count == 0)
			{
				break;
			}
			WallGeom wallGeom = Analyze(wall, RefHeight, out var _);
			if (wallGeom == null)
			{
				continue;
			}
			Side side = wallGeom.Sides.Values.First();
			foreach (FamilySymbol item in Unmeasured)
			{
				_widths[item.Id] = Measure(item, wall, wallGeom, side, wallGeom.Length / 2.0, Height(item));
			}
		}
	}

	public TvTarget RoomTarget(FoundRoom room, int index, List<Outline> doors)
	{
		TvTarget tvTarget = new TvTarget
		{
			Label = RoomLabel(room, index)
		};
		double z = (_doc.GetElement(room.LevelId) as Level)?.ProjectElevation ?? 0.0;
		List<XYZ> list = (from o in doors
			where o.MinimumPoint.Z <= z + 3.0 && o.MaximumPoint.Z >= z + 3.0
			select RoomFinder.Flat((o.MinimumPoint + o.MaximumPoint) / 2.0) into p
			where DistanceToPolygon(p, room.Polygon) < 1.0
			select p).ToList();
		List<Tuple<TvSpot, bool, bool, double>> list2 = new List<Tuple<TvSpot, bool, bool, double>>();
		foreach (RoomWall rw in room.Walls)
		{
			XYZ xYZ = rw.B - rw.A;
			if (xYZ.GetLength() < 0.01)
			{
				continue;
			}
			xYZ = xYZ.Normalize();
			XYZ dir = RoomFinder.Flat(rw.Wall.Orientation).Normalize() * rw.Sign;
			double num = Extent(room.Polygon, xYZ);
			double num2 = Extent(room.Polygon, dir);
			TvSpot spot = new TvSpot
			{
				Wall = rw.Wall,
				Sign = rw.Sign,
				U0 = rw.U0,
				U1 = rw.U1,
				Depth = num2
			};
			Evaluate(spot);
			if (spot.Usable)
			{
				bool item = num2 >= num * 0.95;
				bool item2 = list.Any((XYZ p) => DistanceToSegment(p, rw.A, rw.B) < 1.0);
				double item3 = ((list.Count == 0) ? 0.0 : list.Min((XYZ p) => p.DistanceTo(RoomFinder.Flat(spot.OnFace))));
				list2.Add(Tuple.Create(spot, item, item2, item3));
			}
		}
		tvTarget.Spots = (from r in list2
			orderby r.Item1.Problem == null descending, r.Item2 descending, r.Item3, r.Item4 descending, r.Item1.Depth descending
			select r.Item1).ToList();
		if (tvTarget.Spots.Count == 0)
		{
			tvTarget.Note = "no straight basic wall to hang a TV on";
		}
		else if (tvTarget.Spots.Any((TvSpot s) => s.HasTv))
		{
			tvTarget.Note = "already has a TV";
		}
		else
		{
			tvTarget.Current = 0;
		}
		return tvTarget;
	}

	public TvTarget WallTarget(Wall wall)
	{
		TvTarget tvTarget = new TvTarget
		{
			Label = PlaceArtworkCommand.WallLabel(wall)
		};
		WallGeom g = Analyze(wall, RefHeight, out var error);
		if (g == null)
		{
			tvTarget.Note = error;
			return tvTarget;
		}
		Side first = RoomSide(g) ?? (g.Sides.TryGetValue(-1, out var value) ? value : g.Sides.Values.First());
		List<Side> list = g.Sides.Values.OrderBy((Side s) => (s != first) ? 1 : 0).ToList();
		List<double?> list2 = list.Select((Side s) => RayDepth(g, s)).ToList();
		for (int num = 0; num < list.Count; num++)
		{
			TvSpot tvSpot = new TvSpot
			{
				Wall = wall,
				Sign = list[num].Sign,
				U0 = 0.0,
				U1 = g.Length,
				Depth = list2[num]
			};
			Evaluate(tvSpot);
			tvTarget.Spots.Add(tvSpot);
		}
		if (tvTarget.Spots.Any((TvSpot s) => s.HasTv))
		{
			tvTarget.Note = "already has a TV";
		}
		else
		{
			tvTarget.Current = 0;
		}
		return tvTarget;
	}

	private string RoomLabel(FoundRoom room, int index)
	{
		XYZ xYZ = room.Polygon.Aggregate(XYZ.Zero, (XYZ acc, XYZ p) => acc + p) / room.Polygon.Count;
		if (_doc.GetElement(room.LevelId) is Level level)
		{
			XYZ point = new XYZ(xYZ.X, xYZ.Y, level.ProjectElevation + 3.0);
			Room room2 = ((_phase != null) ? _doc.GetRoomAtPoint(point, _phase) : _doc.GetRoomAtPoint(point));
			if (room2 != null)
			{
				return $"Room {index} \"{room2.Name}\"";
			}
		}
		IEnumerable<string> values = room.Walls.Select((RoomWall w) => w.Wall.Id.Value.ToString()).Take(4);
		return string.Format("Room {0} (walls {1}{2})", index, string.Join(", ", values), (room.Walls.Count > 4) ? "…" : "");
	}

	private double? RayDepth(WallGeom g, Side side)
	{
		if (_view3D == null)
		{
			return null;
		}
		XYZ xYZ = _n * side.Sign;
		XYZ origin = FacePoint(g, side, g.Length / 2.0) + xYZ * 0.05;
		try
		{
			return new ReferenceIntersector(new ElementClassFilter(typeof(Wall)), FindReferenceTarget.Face, _view3D).FindNearest(origin, xYZ)?.Proximity;
		}
		catch
		{
			return null;
		}
	}

	public void Evaluate(TvSpot spot)
	{
		Evaluate(spot, out var _);
	}

	private Side Evaluate(TvSpot spot, out WallGeom g)
	{
		spot.Symbol = null;
		spot.Problem = null;
		spot.HasTv = false;
		g = Analyze(spot.Wall, RefHeight, out var error);
		Side value = null;
		spot.Usable = g != null && g.Sides.TryGetValue(spot.Sign, out value);
		if (!spot.Usable)
		{
			spot.Problem = error ?? "face not found";
			return null;
		}
		double from = Math.Max(0.0, spot.U0);
		double to = Math.Min(g.Length, spot.U1);
		double num = (from + to) / 2.0;
		spot.FaceV = value.V;
		spot.Normal = _n * spot.Sign;
		spot.Along = _d;
		spot.Center = num;
		spot.OnFace = FacePoint(g, value, num);
		spot.HasTv = value.Existing.Any((Tuple<double, double> e) => e.Item2 > from && e.Item1 < to);
		if (spot.HasTv)
		{
			spot.Problem = "already has a TV";
			return null;
		}
		List<Tuple<double, double>> list = FreeSegments(from, to, value.Blocked);
		List<FamilySymbol> list2 = _symbols.Where((FamilySymbol s) => WidthOf(s) > 0.0).OrderBy(WidthOf).ToList();
		if (list2.Count == 0)
		{
			spot.Problem = "none of the TV types could be placed";
			return null;
		}
		List<Tuple<FamilySymbol, double, double>> list3 = new List<Tuple<FamilySymbol, double, double>>();
		foreach (FamilySymbol item in list2)
		{
			if (TryCenter(list, WidthOf(item), num, out var center))
			{
				list3.Add(Tuple.Create(item, WidthOf(item), center));
			}
		}
		if (list3.Count == 0)
		{
			double arg = ((list.Count > 0) ? (list[0].Item2 - list[0].Item1) : 0.0);
			spot.Problem = "free space (" + _fmt(arg) + ") is narrower than the smallest TV (" + _fmt(WidthOf(list2[0])) + ")";
			return null;
		}
		Tuple<FamilySymbol, double, double> tuple = list3.Last();
		if (_autoSize)
		{
			tuple = (spot.Depth.HasValue ? (list3.FirstOrDefault((Tuple<FamilySymbol, double, double> f) => f.Item2 * _depthFactor >= spot.Depth.Value) ?? list3.Last()) : list3.First());
		}
		spot.Symbol = tuple.Item1;
		spot.Width = tuple.Item2;
		spot.Center = tuple.Item3;
		spot.OnFace = FacePoint(g, value, tuple.Item3);
		return value;
	}

	private static bool TryCenter(List<Tuple<double, double>> free, double width, double mid, out double center)
	{
		center = 0.0;
		double num = double.MaxValue;
		foreach (Tuple<double, double> item in free)
		{
			if (!(item.Item2 - item.Item1 + 0.01 < width))
			{
				double num2 = item.Item1 + width / 2.0;
				double num3 = item.Item2 - width / 2.0;
				double num4 = ((num2 > num3) ? ((item.Item1 + item.Item2) / 2.0) : Math.Min(Math.Max(mid, num2), num3));
				if (Math.Abs(num4 - mid) < num)
				{
					num = Math.Abs(num4 - mid);
					center = num4;
				}
			}
		}
		return num < double.MaxValue;
	}

	public FamilyInstance Place(TvSpot spot, out string reason)
	{
		Side side = Evaluate(spot, out var g);
		if (side == null)
		{
			reason = spot.Problem;
			return null;
		}
		FamilyInstance familyInstance = Create(spot.Symbol, spot.Wall, g.Level, side, PointOnFace(g.Level, side, spot.Center, Height(spot.Symbol)), _n * side.Sign);
		if (familyInstance == null)
		{
			reason = "the TV could not be placed on this face";
			return null;
		}
		_doc.Regenerate();
		Box box = PieceBox(familyInstance);
		if (box != null)
		{
			XYZ xYZ = _d * (spot.Center - (box.U0 + box.U1) / 2.0);
			if (_bottom.HasValue)
			{
				xYZ += XYZ.BasisZ * (g.Level.ProjectElevation + _bottom.Value - box.Z0);
			}
			if (!xYZ.IsZeroLength())
			{
				ElementTransformUtils.MoveElement(_doc, familyInstance.Id, xYZ);
			}
		}
		Mark(familyInstance);
		reason = null;
		return familyInstance;
	}

	private XYZ FacePoint(WallGeom g, Side side, double u)
	{
		XYZ xYZ = _p0 + _d * u + _n * side.V;
		return new XYZ(xYZ.X, xYZ.Y, g.RefZ);
	}

	private static double Extent(List<XYZ> poly, XYZ dir)
	{
		List<double> source = poly.Select((XYZ p) => p.DotProduct(dir)).ToList();
		return source.Max() - source.Min();
	}

	private static double DistanceToSegment(XYZ p, XYZ a, XYZ b)
	{
		XYZ xYZ = b - a;
		double num = xYZ.DotProduct(xYZ);
		double num2 = ((num < 1E-09) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (p - a).DotProduct(xYZ) / num)));
		return p.DistanceTo(a + xYZ * num2);
	}

	private static double DistanceToPolygon(XYZ p, List<XYZ> poly)
	{
		return Enumerable.Range(0, poly.Count).Min((int i) => DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Count]));
	}
}
