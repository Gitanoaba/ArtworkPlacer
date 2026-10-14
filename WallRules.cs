using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal class WallInfo
{
	public bool Windows;

	public bool Exterior;

	public bool Core;
}

internal static class WallRules
{
	public static readonly double CoreWidth = UnitUtils.ConvertToInternalUnits(18.0, UnitTypeId.Centimeters);

	// Salas de servicio: palabras completas o abreviadas (STOR., CLEA., IT, WC…) comparadas por palabra
	private static readonly string[] ServiceExact = { "it", "ict", "wc", "idf", "mdf", "ups", "jan", "str", "elec", "mech", "comms", "tel", "telecom" };

	private static readonly string[] ServiceStems = { "toilet", "restroom", "bath", "showe", "lavat", "stor", "clea", "janit", "server", "servic", "electr", "mechan", "shaft", "riser", "stair", "elevat", "lift", "phone", "booth", "archiv", "utilit", "plant", "locker" };

	private static readonly string[] ServiceHebrew = { "שירותים", "מחסן", "מדרגות", "מעלית" };

	private static readonly string[] OfficeWords = { "office", "private", "manager", "director", "ceo", "cfo", "משרד" };

	private static readonly string[] SharedWords = { "open", "workstation", "work station", "desk", "lounge", "reception", "lobby", "corridor", "hall", "kitchen", "pantry" };

	// Muro de ventanas = exterior con ventanas, o con ventanas/vidrio en más de la mitad de su largo.
	// Un muro interior con una ventana chica sigue sirviendo: la ventana solo es un obstáculo.
	public static WallInfo Classify(Wall wall)
	{
		Document doc = wall.Document;
		List<Element> glazing = wall.FindInserts(addRectOpenings: false, includeShadows: false, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: false)
			.Select((ElementId id) => doc.GetElement(id))
			.Where((Element e) => e is Wall || (e?.Category != null && e.Category.Id.Value == (long)BuiltInCategory.OST_Windows))
			.ToList();
		bool exterior = wall.WallType.Function == WallFunction.Exterior;
		bool windows = glazing.Count > 0 && (exterior || GlazedShare(wall, glazing) > 0.5);
		return new WallInfo
		{
			Windows = windows,
			Exterior = exterior,
			Core = !exterior && !windows && wall.Width >= CoreWidth
		};
	}

	private static double GlazedShare(Wall wall, List<Element> glazing)
	{
		if (!(wall.Location is LocationCurve { Curve: Line line }) || line.Length <= 0.0)
		{
			return 0.0;
		}
		XYZ p0 = line.GetEndPoint(0);
		XYZ d = line.Direction;
		List<Tuple<double, double>> spans = new List<Tuple<double, double>>();
		foreach (Element e in glazing)
		{
			BoundingBoxXYZ bb = e.get_BoundingBox(null);
			if (bb == null)
			{
				continue;
			}
			double a = (bb.Min - p0).DotProduct(d);
			double b = (bb.Max - p0).DotProduct(d);
			spans.Add(Tuple.Create(Math.Max(0.0, Math.Min(a, b)), Math.Min(line.Length, Math.Max(a, b))));
		}
		double covered = 0.0;
		double end = double.MinValue;
		foreach (Tuple<double, double> s in spans.OrderBy((Tuple<double, double> s) => s.Item1))
		{
			double from = Math.Max(s.Item1, end);
			if (s.Item2 > from)
			{
				covered += s.Item2 - from;
			}
			end = Math.Max(end, s.Item2);
		}
		return covered / line.Length;
	}

	public static bool IsPrivateOffice(string roomName)
	{
		return Has(roomName, OfficeWords) && !Has(roomName, SharedWords);
	}

	public static bool IsService(string roomName)
	{
		if (roomName == null)
		{
			return false;
		}
		if (Has(roomName, ServiceHebrew))
		{
			return true;
		}
		if (Has(roomName, SharedWords))
		{
			return false;
		}
		return roomName.ToLowerInvariant().Split(new[] { ' ', '.', '-', '_', '/', '(', ')', ',', '&', '+' }, StringSplitOptions.RemoveEmptyEntries)
			.Any((string w) => ServiceExact.Contains(w) || ServiceStems.Any((string s) => w.StartsWith(s, StringComparison.Ordinal)));
	}

	public static bool Has(string text, string[] words)
	{
		return text != null && words.Any((string w) => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
	}
}

// Qué está dentro del edificio en cada nivel, según las losas de piso
internal class Footprint
{
	private readonly Document _doc;

	private readonly Dictionary<long, List<Tuple<BoundingBoxXYZ, Face>>> _tops = new Dictionary<long, List<Tuple<BoundingBoxXYZ, Face>>>();

	public Footprint(Document doc)
	{
		_doc = doc;
	}

	// null = el nivel no tiene losas, no se puede saber
	public bool? Inside(XYZ p, Level level)
	{
		List<Tuple<BoundingBoxXYZ, Face>> tops = Tops(level);
		if (tops.Count == 0)
		{
			return null;
		}
		foreach (Tuple<BoundingBoxXYZ, Face> top in tops)
		{
			BoundingBoxXYZ bb = top.Item1;
			if (p.X < bb.Min.X || p.X > bb.Max.X || p.Y < bb.Min.Y || p.Y > bb.Max.Y)
			{
				continue;
			}
			IntersectionResult r = top.Item2.Project(new XYZ(p.X, p.Y, bb.Max.Z));
			if (r != null && Math.Abs(r.XYZPoint.X - p.X) < 0.05 && Math.Abs(r.XYZPoint.Y - p.Y) < 0.05)
			{
				return true;
			}
		}
		return false;
	}

	private List<Tuple<BoundingBoxXYZ, Face>> Tops(Level level)
	{
		if (_tops.TryGetValue(level.Id.Value, out var cached))
		{
			return cached;
		}
		List<Tuple<BoundingBoxXYZ, Face>> list = new List<Tuple<BoundingBoxXYZ, Face>>();
		foreach (Floor floor in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).Cast<Floor>().Where((Floor f) => f.LevelId == level.Id))
		{
			BoundingBoxXYZ bb = floor.get_BoundingBox(null);
			if (bb == null)
			{
				continue;
			}
			IList<Reference> refs;
			try
			{
				refs = HostObjectUtils.GetTopFaces(floor);
			}
			catch
			{
				continue;
			}
			foreach (Reference r in refs)
			{
				if (floor.GetGeometryObjectFromReference(r) is Face face)
				{
					list.Add(Tuple.Create(bb, face));
				}
			}
		}
		_tops[level.Id.Value] = list;
		return list;
	}
}
