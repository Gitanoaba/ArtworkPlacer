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

	public static readonly string[] NoArtWords = { "toilet", "wc", "restroom", "rest room", "bath", "shower", "lavatory", "storage", "store", "server", "electric", "mechanical", "shaft", "stair", "elevator", "lift", "janitor", "cleaning", "phone", "booth", "archive", "שירותים", "מחסן", "מדרגות", "מעלית" };

	private static readonly string[] OfficeWords = { "office", "private", "manager", "director", "ceo", "cfo", "משרד" };

	private static readonly string[] SharedWords = { "open", "workstation", "work station", "desk", "lounge", "reception", "lobby", "corridor", "kitchen", "pantry" };

	// Muros con ventanas o muro cortina embebido no llevan nada; los gruesos interiores son core
	public static WallInfo Classify(Wall wall)
	{
		Document doc = wall.Document;
		bool windows = wall.FindInserts(addRectOpenings: false, includeShadows: false, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: false)
			.Select((ElementId id) => doc.GetElement(id))
			.Any((Element e) => e is Wall || (e?.Category != null && e.Category.Id.Value == (long)BuiltInCategory.OST_Windows));
		bool exterior = wall.WallType.Function == WallFunction.Exterior;
		return new WallInfo
		{
			Windows = windows,
			Exterior = exterior,
			Core = !exterior && !windows && wall.Width >= CoreWidth
		};
	}

	public static bool IsPrivateOffice(string roomName)
	{
		return Has(roomName, OfficeWords) && !Has(roomName, SharedWords);
	}

	public static bool IsService(string roomName)
	{
		return Has(roomName, NoArtWords);
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
