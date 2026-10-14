using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace ArtworkPlacer;

internal abstract class WallFaces
{
	protected class Side
	{
		public int Sign;

		public double V;

		public List<Reference> Refs = new List<Reference>();

		public List<PlanarFace> Faces = new List<PlanarFace>();

		public List<Tuple<double, double>> Blocked = new List<Tuple<double, double>>();

		public List<Tuple<double, double>> Existing = new List<Tuple<double, double>>();

		public bool HasExisting => Existing.Count > 0;

		public string Name
		{
			get
			{
				if (Sign <= 0)
				{
					return "interior face";
				}
				return "exterior face";
			}
		}
	}

	protected class Box
	{
		public double U0;

		public double U1;

		public double V0;

		public double V1;

		public double Z0;

		public double Z1;
	}

	protected class WallGeom
	{
		public Level Level;

		public double Length;

		public double RefZ;

		public Dictionary<int, Side> Sides = new Dictionary<int, Side>();
	}

	protected const double Eps = 0.01;

	protected readonly Document _doc;

	protected readonly List<FamilySymbol> _symbols;

	protected readonly double _clearance;

	protected readonly Phase _phase;

	protected readonly Dictionary<ElementId, double> _widths = new Dictionary<ElementId, double>();

	private readonly string _marker;

	private readonly HashSet<ElementId> _ownFamilyIds;

	private readonly Dictionary<ElementId, List<FamilyInstance>> _hostedByWall;

	private readonly List<FamilyInstance> _looseOwn;

	protected XYZ _p0;

	protected XYZ _d;

	protected XYZ _n;

	protected WallFaces(Document doc, List<FamilySymbol> symbols, double clearance, string marker)
	{
		_doc = doc;
		_symbols = symbols;
		_clearance = clearance;
		_marker = marker;
		_ownFamilyIds = new HashSet<ElementId>(symbols.Select((FamilySymbol s) => s.Family.Id));
		ElementId elementId = ((Element)doc.ActiveView)?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
		_phase = ((elementId != null) ? (doc.GetElement(elementId) as Phase) : null);
		List<FamilyInstance> source = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
		_hostedByWall = (from fi in source
			where fi.Host is Wall
			group fi by fi.Host.Id).ToDictionary((IGrouping<ElementId, FamilyInstance> g) => g.Key, (IGrouping<ElementId, FamilyInstance> g) => g.ToList());
		_looseOwn = source.Where((FamilyInstance fi) => !(fi.Host is Wall) && IsOwn(fi)).ToList();
	}

	private bool IsOwn(FamilyInstance fi)
	{
		if (!_ownFamilyIds.Contains(fi.Symbol.Family.Id))
		{
			return ((Element)fi).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == _marker;
		}
		return true;
	}

	protected void Mark(FamilyInstance fi)
	{
		((Element)fi).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(_marker);
	}

	protected WallGeom Analyze(Wall wall, double refHeight, out string error)
	{
		error = null;
		if (wall.WallType.Kind != WallKind.Basic)
		{
			error = "curtain/stacked walls are not supported";
			return null;
		}
		if (!(wall.Location is LocationCurve { Curve: Line curve }))
		{
			error = "curved walls are not supported";
			return null;
		}
		if (!(_doc.GetElement(wall.LevelId) is Level level))
		{
			error = "wall has no level";
			return null;
		}
		_p0 = curve.GetEndPoint(0);
		_d = curve.Direction;
		_n = wall.Orientation;
		WallGeom wallGeom = new WallGeom
		{
			Level = level,
			Length = curve.Length,
			RefZ = level.ProjectElevation + refHeight
		};
		AddSide(wall, ShellLayerType.Exterior, 1, wallGeom.Sides);
		AddSide(wall, ShellLayerType.Interior, -1, wallGeom.Sides);
		if (wallGeom.Sides.Count == 0)
		{
			error = "no planar faces found";
			return null;
		}
		CollectObstacles(wall, wallGeom.Sides, wallGeom.RefZ, wallGeom.Length);
		return wallGeom;
	}

	private void AddSide(Wall wall, ShellLayerType shell, int sign, Dictionary<int, Side> sides)
	{
		IList<Reference> sideFaces;
		try
		{
			sideFaces = HostObjectUtils.GetSideFaces(wall, shell);
		}
		catch
		{
			return;
		}
		Side side = new Side
		{
			Sign = sign
		};
		foreach (Reference item2 in sideFaces)
		{
			if (wall.GetGeometryObjectFromReference(item2) is PlanarFace item)
			{
				side.Refs.Add(item2);
				side.Faces.Add(item);
			}
		}
		if (side.Faces.Count != 0)
		{
			side.V = (side.Faces[0].Origin - _p0).DotProduct(_n);
			sides[sign] = side;
		}
	}

	private void CollectObstacles(Wall wall, Dictionary<int, Side> sides, double refZ, double length)
	{
		HashSet<ElementId> insertIds = new HashSet<ElementId>(wall.FindInserts(addRectOpenings: true, includeShadows: false, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: true));
		foreach (ElementId item in insertIds)
		{
			Box box = LocalBox(_doc.GetElement(item));
			if (box == null)
			{
				continue;
			}
			foreach (Side value2 in sides.Values)
			{
				value2.Blocked.Add(Tuple.Create(box.U0, box.U1));
			}
		}
		if (_hostedByWall.TryGetValue(wall.Id, out var value))
		{
			foreach (FamilyInstance item2 in value)
			{
				if (insertIds.Contains(item2.Id))
				{
					continue;
				}
				Box box2 = LocalBox(item2);
				if (box2 == null)
				{
					continue;
				}
				bool flag = IsOwn(item2);
				foreach (Side item3 in AffectedSides(box2, sides, insideBlocksBoth: true))
				{
					item3.Blocked.Add(Tuple.Create(box2.U0, box2.U1));
					if (flag)
					{
						item3.Existing.Add(Tuple.Create(box2.U0, box2.U1));
					}
				}
			}
		}
		BoundingBoxXYZ boundingBoxXYZ = ((Element)wall).get_BoundingBox((View)null);
		if (boundingBoxXYZ == null)
		{
			return;
		}
		foreach (FamilyInstance item4 in _looseOwn)
		{
			Box box3 = LocalBox(item4);
			if (box3 == null || box3.U1 < 0.0 || box3.U0 > length || box3.Z1 < boundingBoxXYZ.Min.Z || box3.Z0 > boundingBoxXYZ.Max.Z)
			{
				continue;
			}
			foreach (Side value3 in sides.Values)
			{
				bool num;
				if (value3.Sign <= 0)
				{
					if (!(box3.V0 < value3.V - 0.01))
					{
						continue;
					}
					num = box3.V1 > value3.V - 0.5;
				}
				else
				{
					if (!(box3.V1 > value3.V + 0.01))
					{
						continue;
					}
					num = box3.V0 < value3.V + 0.5;
				}
				if (num)
				{
					value3.Blocked.Add(Tuple.Create(box3.U0, box3.U1));
					value3.Existing.Add(Tuple.Create(box3.U0, box3.U1));
				}
			}
		}
		XYZ xYZ = new XYZ(0.1, 0.1, 0.1);
		foreach (Element item5 in from e in new FilteredElementCollector(_doc).OfClass(typeof(Wall)).WherePasses(new BoundingBoxIntersectsFilter(new Outline(boundingBoxXYZ.Min - xYZ, boundingBoxXYZ.Max + xYZ)))
			where e.Id != wall.Id && !insertIds.Contains(e.Id)
			select e)
		{
			Box box4 = LocalBox(item5);
			if (box4 == null || box4.Z0 > refZ || box4.Z1 < refZ)
			{
				continue;
			}
			foreach (Side item6 in AffectedSides(box4, sides, insideBlocksBoth: false))
			{
				item6.Blocked.Add(Tuple.Create(box4.U0, box4.U1));
			}
		}
	}

	private static IEnumerable<Side> AffectedSides(Box b, Dictionary<int, Side> sides, bool insideBlocksBoth)
	{
		List<Side> list = new List<Side>();
		if (sides.TryGetValue(1, out var value) && b.V1 > value.V + 0.01)
		{
			list.Add(value);
		}
		if (sides.TryGetValue(-1, out var value2) && b.V0 < value2.V - 0.01)
		{
			list.Add(value2);
		}
		if ((list.Count == 0) & insideBlocksBoth)
		{
			list.AddRange(sides.Values);
		}
		return list;
	}

	protected Side RoomSide(WallGeom g)
	{
		XYZ xYZ = _p0 + _d * (g.Length / 2.0);
		double z = g.Level.ProjectElevation + 3.0;
		Side result = null;
		double num = -1.0;
		foreach (Side value in g.Sides.Values)
		{
			XYZ xYZ2 = xYZ + _n * (value.V + (double)value.Sign * 1.0);
			xYZ2 = new XYZ(xYZ2.X, xYZ2.Y, z);
			Room room = ((_phase != null) ? _doc.GetRoomAtPoint(xYZ2, _phase) : _doc.GetRoomAtPoint(xYZ2));
			if (room != null && room.Area > num)
			{
				result = value;
				num = room.Area;
			}
		}
		return result;
	}

	protected List<Tuple<double, double>> FreeSegments(double from, double to, List<Tuple<double, double>> blocked)
	{
		IOrderedEnumerable<Tuple<double, double>> orderedEnumerable = from b in blocked
			select Tuple.Create(Math.Max(@from, b.Item1 - _clearance), Math.Min(to, b.Item2 + _clearance)) into b
			where b.Item2 > b.Item1
			orderby b.Item1
			select b;
		List<Tuple<double, double>> list = new List<Tuple<double, double>>();
		double num = from + _clearance;
		foreach (Tuple<double, double> item in orderedEnumerable)
		{
			if (item.Item1 - num > 0.01)
			{
				list.Add(Tuple.Create(num, item.Item1));
			}
			num = Math.Max(num, item.Item2);
		}
		if (to - _clearance - num > 0.01)
		{
			list.Add(Tuple.Create(num, to - _clearance));
		}
		return list.OrderByDescending((Tuple<double, double> f) => f.Item2 - f.Item1).ToList();
	}

	protected XYZ PointOnFace(Level level, Side side, double u, double heightAboveLevel)
	{
		XYZ xYZ = _p0 + _d * u + _n * side.V;
		return new XYZ(xYZ.X, xYZ.Y, level.ProjectElevation + heightAboveLevel);
	}

	protected double Measure(FamilySymbol sym, Wall wall, WallGeom g, Side side, double u, double heightAboveLevel)
	{
		try
		{
			FamilyInstance familyInstance = Create(sym, wall, g.Level, side, PointOnFace(g.Level, side, u, heightAboveLevel), _n * side.Sign);
			if (familyInstance == null)
			{
				return -1.0;
			}
			_doc.Regenerate();
			Box box = PieceBox(familyInstance);
			_doc.Delete(familyInstance.Id);
			return (box == null) ? (-1.0) : (box.U1 - box.U0);
		}
		catch
		{
			return -1.0;
		}
	}

	protected FamilyInstance Create(FamilySymbol sym, Wall wall, Level level, Side side, XYZ pt, XYZ faceN)
	{
		switch (sym.Family.FamilyPlacementType)
		{
		case FamilyPlacementType.WorkPlaneBased:
		{
			Reference reference = side.Refs[0];
			for (int i = 0; i < side.Faces.Count; i++)
			{
				if (side.Faces[i].Project(pt) != null)
				{
					reference = side.Refs[i];
					break;
				}
			}
			XYZ referenceDirection = XYZ.BasisZ.CrossProduct(faceN);
			return _doc.Create.NewFamilyInstance(reference, pt, referenceDirection, sym);
		}
		case FamilyPlacementType.OneLevelBasedHosted:
		{
			FamilyInstance familyInstance2 = _doc.Create.NewFamilyInstance(pt, sym, wall, level, StructuralType.NonStructural);
			_doc.Regenerate();
			if (familyInstance2.FacingOrientation.DotProduct(faceN) < 0.0 && familyInstance2.CanFlipFacing)
			{
				familyInstance2.flipFacing();
			}
			return familyInstance2;
		}
		case FamilyPlacementType.OneLevelBased:
		{
			XYZ xYZ = new XYZ(pt.X, pt.Y, level.ProjectElevation);
			FamilyInstance familyInstance = _doc.Create.NewFamilyInstance(xYZ, sym, level, StructuralType.NonStructural);
			((Element)familyInstance).get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)?.Set(pt.Z - level.ProjectElevation);
			_doc.Regenerate();
			double num = familyInstance.FacingOrientation.AngleOnPlaneTo(faceN, XYZ.BasisZ);
			if (Math.Abs(num) > 1E-06)
			{
				ElementTransformUtils.RotateElement(_doc, familyInstance.Id, Line.CreateBound(xYZ, xYZ + XYZ.BasisZ), num);
			}
			return familyInstance;
		}
		default:
			return null;
		}
	}

	protected static double DefaultElevation(FamilySymbol sym)
	{
		Parameter parameter = sym.LookupParameter("Default Elevation");
		if (parameter != null && parameter.StorageType == StorageType.Double)
		{
			return parameter.AsDouble();
		}
		return UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Centimeters);
	}

	protected Box LocalBox(Element e)
	{
		BoundingBoxXYZ boundingBoxXYZ = e?.get_BoundingBox((View)null);
		if (boundingBoxXYZ == null)
		{
			return null;
		}
		List<XYZ> list = new List<XYZ>();
		for (int i = 0; i < 8; i++)
		{
			XYZ point = new XYZ(((i & 1) == 0) ? boundingBoxXYZ.Min.X : boundingBoxXYZ.Max.X, ((i & 2) == 0) ? boundingBoxXYZ.Min.Y : boundingBoxXYZ.Max.Y, ((i & 4) == 0) ? boundingBoxXYZ.Min.Z : boundingBoxXYZ.Max.Z);
			list.Add(boundingBoxXYZ.Transform.OfPoint(point));
		}
		return ToBox(list);
	}

	protected Box PieceBox(Element e)
	{
		List<XYZ> list = ArtGeometry.VisiblePoints(e);
		if (list.Count <= 0)
		{
			return LocalBox(e);
		}
		return ToBox(list);
	}

	private Box ToBox(IEnumerable<XYZ> points)
	{
		Box box = new Box
		{
			U0 = double.MaxValue,
			U1 = double.MinValue,
			V0 = double.MaxValue,
			V1 = double.MinValue,
			Z0 = double.MaxValue,
			Z1 = double.MinValue
		};
		foreach (XYZ point in points)
		{
			XYZ xYZ = point - _p0;
			double val = xYZ.DotProduct(_d);
			double val2 = xYZ.DotProduct(_n);
			box.U0 = Math.Min(box.U0, val);
			box.U1 = Math.Max(box.U1, val);
			box.V0 = Math.Min(box.V0, val2);
			box.V1 = Math.Max(box.V1, val2);
			box.Z0 = Math.Min(box.Z0, point.Z);
			box.Z1 = Math.Max(box.Z1, point.Z);
		}
		return box;
	}
}
