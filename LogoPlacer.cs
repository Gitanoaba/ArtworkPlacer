using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace ArtworkPlacer;

internal class LogoSpot
{
	public Wall Wall;

	public string Label;

	public XYZ Center;

	public XYZ Along;

	public XYZ Normal;

	public double Width;

	public double Height;

	public double FreeLength;
}

// Guía rosada que dejó la versión anterior (con decals) en el modelo
internal class LogoGuide
{
	public List<ElementId> Curves = new List<ElementId>();

	public XYZ Center;

	public XYZ Normal;

	public double Width;

	public double Height;
}

// Elige el muro del logo (recepción/lobby) y encuentra la imagen del cliente.
// Las guías rosadas eran del primer intento con decals: solo se buscan para borrarlas.
internal class LogoPlacer : WallFaces
{
	public const string GuideStyle = "Qbiq Logo Guide";

	private static readonly string[] ReceptionWords = { "reception", "lobby", "entrance", "entry", "welcome", "front desk", "קבלה", "לובי" };

	private static readonly Regex ImagePath = new Regex("[A-Za-z]:\\\\[^\"<>|*?\\r\\n]+?\\.(png|jpe?g|bmp|tiff?)", RegexOptions.IgnoreCase);

	public LogoPlacer(Document doc, double clearance)
		: base(doc, new List<FamilySymbol>(), clearance, "QbiqLogo")
	{
	}

	// Centro del tramo libre más largo de esa cara (sign null = la cara que mira al room más grande)
	public LogoSpot SpotOn(Wall wall, int? sign, double? from, double? to, double centerHeight, double width, double height, out string why)
	{
		WallGeom g = Analyze(wall, centerHeight, out why);
		if (g == null)
		{
			return null;
		}
		Side side = sign.HasValue ? (g.Sides.TryGetValue(sign.Value, out var s) ? s : null) : (RoomSide(g) ?? (g.Sides.TryGetValue(-1, out var inner) ? inner : g.Sides.Values.First()));
		if (side == null)
		{
			why = "that side of the wall has no flat face";
			return null;
		}
		Tuple<double, double> seg = FreeSegments(Math.Max(0.0, from ?? 0.0), Math.Min(g.Length, to ?? g.Length), side.Blocked).FirstOrDefault();
		if (seg == null || seg.Item2 - seg.Item1 < width)
		{
			why = "not enough free wall for the logo";
			return null;
		}
		double u = (seg.Item1 + seg.Item2) / 2.0;
		return new LogoSpot
		{
			Wall = wall,
			Center = PointOnFace(g.Level, side, u, centerHeight),
			Along = _d,
			Normal = _n * side.Sign,
			Width = width,
			Height = height,
			FreeLength = seg.Item2 - seg.Item1
		};
	}

	public static bool IsReception(string roomName)
	{
		return WallRules.Has(roomName, ReceptionWords);
	}

	// El mejor muro de recepción/lobby: el tramo libre más largo, sin muros de ventanas
	public LogoSpot ReceptionSpot(List<Room> rooms, double centerHeight, double width, double height)
	{
		SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions
		{
			SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
		};
		LogoSpot best = null;
		foreach (Room room in rooms)
		{
			string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Reception";
			foreach (RoomFace face in RoomScope.Faces(room, options))
			{
				if (face.Wall.WallType.Kind != WallKind.Basic || WallRules.Classify(face.Wall).Windows)
				{
					continue;
				}
				LogoSpot spot = SpotOn(face.Wall, face.Sign, face.U0, face.U1, centerHeight, width, height, out _);
				if (spot != null && (best == null || spot.FreeLength > best.FreeLength))
				{
					spot.Label = name;
					best = spot;
				}
			}
		}
		return best;
	}

	// Ruta del logo, en orden: la carpeta del Design ID del add-in de Qbiq, un texto o imagen del modelo con la ruta,
	// la carpeta del proyecto y, por último, el *_client más reciente en la carpeta del usuario
	public static string FindLogo(Autodesk.Revit.UI.UIApplication uiapp, Document doc)
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string id = DesignId(uiapp);
		if (!string.IsNullOrEmpty(id))
		{
			string byId = ClientImage(Path.Combine(home, id));
			if (byId != null)
			{
				return byId;
			}
		}
		string inModel = FindLogo(doc);
		if (inModel != null)
		{
			return inModel;
		}
		try
		{
			return Directory.GetDirectories(home).Select(ClientImage).Where((string f) => f != null)
				.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}

	private static string ClientImage(string folder)
	{
		try
		{
			return Directory.Exists(folder) ? Directory.GetFiles(folder, "*_client.*").FirstOrDefault((string f) => ImagePath.IsMatch(f) && f.IndexOf("_logo_", StringComparison.OrdinalIgnoreCase) < 0) : null;
		}
		catch
		{
			return null;
		}
	}

	// El Design ID que muestra el add-in de Qbiq en su panel del ribbon
	public static string DesignId(Autodesk.Revit.UI.UIApplication uiapp)
	{
		try
		{
			foreach (Autodesk.Revit.UI.RibbonPanel panel in uiapp.GetRibbonPanels("Qbiq"))
			{
				foreach (Autodesk.Revit.UI.RibbonItem item in panel.GetItems())
				{
					if (item is Autodesk.Revit.UI.TextBox tb && tb.Name.IndexOf("design", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return tb.Value?.ToString()?.Trim();
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public static string FindLogo(Document doc)
	{
		List<string> found = new List<string>();
		foreach (ImageType img in new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>())
		{
			try
			{
				if (!string.IsNullOrEmpty(img.Path) && File.Exists(img.Path) && img.Path.IndexOf("_client", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					found.Add(img.Path);
				}
			}
			catch
			{
			}
		}
		foreach (TextNote note in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>())
		{
			string text = (note.Text ?? "").Replace("\r", "").Replace("\n", "");
			foreach (Match m in ImagePath.Matches(text))
			{
				if (File.Exists(m.Value))
				{
					found.Add(m.Value);
				}
			}
		}
		string fromNote = found.OrderBy((string p) => (p.IndexOf("_client", StringComparison.OrdinalIgnoreCase) >= 0) ? 0 : 1).FirstOrDefault();
		if (fromNote != null)
		{
			return fromNote;
		}
		if (string.IsNullOrEmpty(doc.PathName))
		{
			return null;
		}
		string dir = Path.GetDirectoryName(doc.PathName);
		foreach (string folder in new[] { dir, Path.GetDirectoryName(dir) }.Where((string f) => !string.IsNullOrEmpty(f) && Directory.Exists(f)))
		{
			foreach (string pattern in new[] { "*_client.*", "*logo*.*" })
			{
				string hit = Directory.GetFiles(folder, pattern).FirstOrDefault((string f) => ImagePath.IsMatch(f));
				if (hit != null)
				{
					return hit;
				}
			}
		}
		return null;
	}

	public static List<LogoGuide> FindGuides(Document doc)
	{
		List<LogoGuide> guides = new List<LogoGuide>();
		foreach (IGrouping<ElementId, ModelCurve> g in from mc in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).OfType<ModelCurve>()
			where mc.LineStyle?.Name == GuideStyle
			group mc by mc.SketchPlane.Id)
		{
			List<Curve> curves = g.Select((ModelCurve mc) => mc.GeometryCurve).ToList();
			List<XYZ> ends = curves.SelectMany((Curve c) => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }).ToList();
			XYZ center = new XYZ(ends.Average((XYZ e) => e.X), ends.Average((XYZ e) => e.Y), ends.Average((XYZ e) => e.Z));
			guides.Add(new LogoGuide
			{
				Curves = g.Select((ModelCurve mc) => mc.Id).ToList(),
				Center = center,
				Normal = g.First().SketchPlane.GetPlane().Normal,
				Width = curves.Where((Curve c) => Math.Abs((c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize().Z) < 0.01).Select((Curve c) => c.Length).DefaultIfEmpty(0.0).Max(),
				Height = curves.Where((Curve c) => Math.Abs((c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize().Z) > 0.99).Select((Curve c) => c.Length).DefaultIfEmpty(0.0).Max()
			});
		}
		return guides;
	}


}
