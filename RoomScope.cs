using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace ArtworkPlacer;

internal class FaceRanges
{
	private readonly Dictionary<Tuple<ElementId, int>, List<Tuple<double, double>>> _ranges = new Dictionary<Tuple<ElementId, int>, List<Tuple<double, double>>>();

	public void Add(ElementId wall, int sign, double u0, double u1)
	{
		Tuple<ElementId, int> key = Tuple.Create(wall, sign);
		if (!_ranges.TryGetValue(key, out var list))
		{
			list = (_ranges[key] = new List<Tuple<double, double>>());
		}
		list.Add(Tuple.Create(u0, u1));
		_ranges[key] = Merge(list);
	}

	public List<int> Signs(ElementId wall)
	{
		return (from k in _ranges.Keys
			where k.Item1 == wall
			select k.Item2).OrderBy((int s) => s).ToList();
	}

	public List<Tuple<double, double>> Get(ElementId wall, int sign, double length)
	{
		if (_ranges.TryGetValue(Tuple.Create(wall, sign), out var list))
		{
			return list;
		}
		List<Tuple<double, double>> other = Merge((from kv in _ranges
			where kv.Key.Item1 == wall
			select kv.Value).SelectMany((List<Tuple<double, double>> l) => l).ToList());
		if (other.Count <= 0)
		{
			return new List<Tuple<double, double>> { Tuple.Create(0.0, length) };
		}
		return other;
	}

	private static List<Tuple<double, double>> Merge(List<Tuple<double, double>> ranges)
	{
		List<Tuple<double, double>> merged = new List<Tuple<double, double>>();
		foreach (Tuple<double, double> r in ranges.OrderBy((Tuple<double, double> r) => r.Item1))
		{
			Tuple<double, double> last = merged.LastOrDefault();
			if (last != null && r.Item1 <= last.Item2 + 0.01)
			{
				merged[merged.Count - 1] = Tuple.Create(last.Item1, Math.Max(last.Item2, r.Item2));
			}
			else
			{
				merged.Add(r);
			}
		}
		return merged;
	}
}

internal class RoomFace
{
	public Wall Wall;

	public int Sign;

	public double U0;

	public double U1;

	public XYZ A;

	public XYZ B;

	public XYZ Normal;
}

internal static class RoomScope
{
	private const double MinRun = 0.1;

	private static readonly string[] DryWords = { "drywall", "dry wall", "gypsum", "gyp", "plasterboard", "plaster board", "gwb", "sheetrock", "gips", "גבס" };

	public static List<RoomFace> Faces(Room room, SpatialElementBoundaryOptions options)
	{
		Document doc = room.Document;
		List<RoomFace> faces = new List<RoomFace>();
		IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);
		if (loops == null)
		{
			return faces;
		}
		foreach (IGrouping<ElementId, BoundarySegment> group in from s in loops.SelectMany((IList<BoundarySegment> l) => l)
			where doc.GetElement(s.ElementId) is Wall
			group s by s.ElementId)
		{
			Wall wall = (Wall)doc.GetElement(group.Key);
			if (!(wall.Location is LocationCurve { Curve: Line line }))
			{
				continue;
			}
			XYZ p0 = line.GetEndPoint(0);
			XYZ d = line.Direction;
			foreach (BoundarySegment seg in group)
			{
				Curve c = seg.GetCurve();
				XYZ a = c.GetEndPoint(0);
				XYZ b = c.GetEndPoint(1);
				double ua = (a - p0).DotProduct(d);
				double ub = (b - p0).DotProduct(d);
				int sign = SideFacing(room, c.Evaluate(0.5, normalized: true), wall.Orientation, p0);
				RoomFace face = new RoomFace
				{
					Wall = wall,
					Sign = sign,
					U0 = Math.Max(0.0, Math.Min(ua, ub)),
					U1 = Math.Min(line.Length, Math.Max(ua, ub)),
					A = (ua <= ub) ? a : b,
					B = (ua <= ub) ? b : a,
					Normal = wall.Orientation * sign
				};
				RoomFace prev = faces.LastOrDefault((RoomFace f) => f.Wall.Id == wall.Id && f.Sign == sign && Math.Abs(f.U1 - face.U0) < 0.05);
				if (prev != null)
				{
					prev.U1 = face.U1;
					prev.B = face.B;
				}
				else if (face.U1 - face.U0 >= MinRun)
				{
					faces.Add(face);
				}
			}
		}
		return faces;
	}

	public static bool IsDrywall(Wall wall, int sign)
	{
		Document doc = wall.Document;
		CompoundStructure cs = wall.WallType.GetCompoundStructure();
		List<CompoundStructureLayer> layers = cs?.GetLayers().ToList() ?? new List<CompoundStructureLayer>();
		if (layers.Count > 0)
		{
			int first = cs.GetFirstCoreLayerIndex();
			int last = cs.GetLastCoreLayerIndex();
			IEnumerable<int> side = (sign > 0) ? Enumerable.Range(0, Math.Max(1, first + 1)) : Enumerable.Range(Math.Min(last, layers.Count - 1), layers.Count - Math.Min(last, layers.Count - 1));
			List<string> texts = (from i in side
				let m = doc.GetElement(layers[i].MaterialId) as Material
				where m != null
				select m.Name + " " + m.MaterialClass + " " + m.MaterialCategory).ToList();
			if (texts.Any(HasDryWord))
			{
				return true;
			}
			if (texts.Count > 0)
			{
				return false;
			}
		}
		return HasDryWord(wall.WallType.Name);
	}

	private static bool HasDryWord(string text)
	{
		return text != null && DryWords.Any((string w) => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	public static List<RoomChoice> Collect(Document doc, Func<double, string> fmtArea, HashSet<long> lastRooms)
	{
		return (from Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
			where r.Location != null && r.Area > 0.0
			select new RoomChoice
			{
				Id = r.Id.Value,
				Name = Clean(r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()) ?? "Unnamed room",
				Number = Clean(r.Number) ?? "",
				Level = r.Level?.Name ?? "",
				AreaText = fmtArea(r.Area),
				IsChecked = lastRooms.Contains(r.Id.Value)
			} into c
			orderby c.Name, c.Number.Length, c.Number
			select c).ToList();
	}

	public static List<Wall> Walls(Document doc, IEnumerable<long> roomIds, FaceRanges ranges)
	{
		SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions
		{
			SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
		};
		Dictionary<ElementId, Wall> walls = new Dictionary<ElementId, Wall>();
		foreach (Room room in roomIds.Select((long id) => doc.GetElement(new ElementId(id))).OfType<Room>())
		{
			IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);
			if (loops == null)
			{
				continue;
			}
			foreach (BoundarySegment seg in loops.SelectMany((IList<BoundarySegment> l) => l))
			{
				if (!(doc.GetElement(seg.ElementId) is Wall wall) || !(wall.Location is LocationCurve { Curve: Line line }))
				{
					continue;
				}
				Curve curve = seg.GetCurve();
				XYZ p0 = line.GetEndPoint(0);
				XYZ d = line.Direction;
				double a = (curve.GetEndPoint(0) - p0).DotProduct(d);
				double b = (curve.GetEndPoint(1) - p0).DotProduct(d);
				double u0 = Math.Max(0.0, Math.Min(a, b));
				double u1 = Math.Min(line.Length, Math.Max(a, b));
				if (u1 - u0 < MinRun)
				{
					continue;
				}
				walls[wall.Id] = wall;
				ranges.Add(wall.Id, SideFacing(room, curve.Evaluate(0.5, normalized: true), wall.Orientation, p0), u0, u1);
			}
		}
		return walls.Values.ToList();
	}

	public static FoundRoom Found(Room room, SpatialElementBoundaryOptions options)
	{
		Document doc = room.Document;
		FoundRoom found = new FoundRoom
		{
			LevelId = room.LevelId,
			Area = room.Area
		};
		IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);
		if (loops == null || loops.Count == 0)
		{
			return found;
		}
		found.Polygon = loops[0].Select((BoundarySegment s) => RoomFinder.Flat(s.GetCurve().GetEndPoint(0))).ToList();
		foreach (IGrouping<ElementId, BoundarySegment> group in from s in loops.SelectMany((IList<BoundarySegment> l) => l)
			where doc.GetElement(s.ElementId) is Wall
			group s by s.ElementId)
		{
			Wall wall = (Wall)doc.GetElement(group.Key);
			if (!(wall.Location is LocationCurve { Curve: Line line }))
			{
				continue;
			}
			XYZ p0 = line.GetEndPoint(0);
			XYZ d = line.Direction;
			List<double> us = (from s in @group
				from p in new[] { s.GetCurve().GetEndPoint(0), s.GetCurve().GetEndPoint(1) }
				select (p - p0).DotProduct(d)).ToList();
			double u0 = Math.Max(0.0, us.Min());
			double u1 = Math.Min(line.Length, us.Max());
			if (u1 - u0 < MinRun)
			{
				continue;
			}
			found.Walls.Add(new RoomWall
			{
				Wall = wall,
				Sign = SideFacing(room, group.First().GetCurve().Evaluate(0.5, normalized: true), wall.Orientation, p0),
				U0 = u0,
				U1 = u1,
				A = RoomFinder.Flat(p0 + d * u0),
				B = RoomFinder.Flat(p0 + d * u1)
			});
		}
		return found;
	}

	private static int SideFacing(Room room, XYZ mid, XYZ n, XYZ p0)
	{
		XYZ probe = new XYZ(mid.X, mid.Y, mid.Z + 1.0);
		if (room.IsPointInRoom(probe + n * 0.5))
		{
			return 1;
		}
		if (room.IsPointInRoom(probe - n * 0.5))
		{
			return -1;
		}
		return ((mid - p0).DotProduct(n) >= 0.0) ? 1 : (-1);
	}

	private static string Clean(string s)
	{
		return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
	}
}
