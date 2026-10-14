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

// Guía ya dibujada en el modelo: un rectángulo con diagonales en el plano del muro
internal class LogoGuide
{
	public List<ElementId> Curves = new List<ElementId>();

	public XYZ Center;

	public XYZ Normal;

	public double Width;

	public double Height;
}

// La API de Revit 2024 no puede crear ni colocar decals: aquí se elige el lugar, se marca con una guía
// y, una vez que el usuario coloca el decal, se centra y se ajusta su tamaño a la guía.
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

	// Ruta del logo: el texto con la ruta que deja el add-in de Qbiq, o la carpeta del proyecto
	public static string FindLogo(Document doc)
	{
		List<string> found = new List<string>();
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

	public static List<Element> LogoDecalTypes(Document doc)
	{
		return (from e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DecalType).WhereElementIsElementType()
			where e.Name.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0
			select e).ToList();
	}

	private static GraphicsStyle GuideLineStyle(Document doc)
	{
		Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
		Category sub = lines.SubCategories.Cast<Category>().FirstOrDefault((Category c) => c.Name == GuideStyle);
		if (sub == null)
		{
			sub = doc.Settings.Categories.NewSubcategory(lines, GuideStyle);
			sub.LineColor = new Color(230, 0, 90);
			sub.SetLineWeight(6, GraphicsStyleType.Projection);
		}
		return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
	}

	public static List<ElementId> DrawGuide(Document doc, LogoSpot s)
	{
		GraphicsStyle style = GuideLineStyle(doc);
		XYZ c = s.Center + s.Normal * 0.03;
		SketchPlane plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(s.Normal, c));
		XYZ a = s.Along * (s.Width / 2.0);
		XYZ up = XYZ.BasisZ * (s.Height / 2.0);
		XYZ[] p = { c - a - up, c + a - up, c + a + up, c - a + up };
		List<ElementId> ids = new List<ElementId>();
		foreach (Tuple<int, int> e in new[] { Tuple.Create(0, 1), Tuple.Create(1, 2), Tuple.Create(2, 3), Tuple.Create(3, 0), Tuple.Create(0, 2), Tuple.Create(1, 3) })
		{
			ModelCurve mc = doc.Create.NewModelCurve(Line.CreateBound(p[e.Item1], p[e.Item2]), plane);
			mc.LineStyle = style;
			ids.Add(mc.Id);
		}
		return ids;
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

	// Centra y dimensiona el decal más cercano a la guía. Devuelve null si salió bien, si no el motivo
	public static string Snap(Document doc, LogoGuide guide, List<Element> decals)
	{
		Element decal = decals.Select((Element d) => new { d, c = Center(d) })
			.Where(x => x.c != null && Math.Abs((x.c - guide.Center).DotProduct(guide.Normal)) < 1.0 && Flat(x.c - guide.Center, guide.Normal).GetLength() < 10.0)
			.OrderBy(x => Flat(x.c - guide.Center, guide.Normal).GetLength())
			.Select(x => x.d)
			.FirstOrDefault();
		if (decal == null)
		{
			return "no decal near the guide. Place the logo decal inside the pink rectangle first";
		}
		Parameter width = decal.get_Parameter(BuiltInParameter.DECAL_WIDTH);
		Parameter height = decal.get_Parameter(BuiltInParameter.DECAL_HEIGHT);
		bool locked = decal.get_Parameter(BuiltInParameter.DECAL_LOCK_PROPORTIONS)?.AsInteger() == 1;
		if (width != null && !width.IsReadOnly && guide.Width > 0.0)
		{
			width.Set(guide.Width);
		}
		if (!locked && height != null && !height.IsReadOnly && guide.Height > 0.0)
		{
			height.Set(guide.Height);
		}
		doc.Regenerate();
		XYZ c = Center(decal);
		XYZ move = Flat(guide.Center - c, guide.Normal);
		if (!move.IsZeroLength())
		{
			ElementTransformUtils.MoveElement(doc, decal.Id, move);
		}
		return null;
	}

	private static XYZ Center(Element e)
	{
		BoundingBoxXYZ bb = e.get_BoundingBox(null);
		return (bb == null) ? null : ((bb.Min + bb.Max) / 2.0);
	}

	// Vector sin la componente normal al muro (movimiento dentro del plano del muro)
	private static XYZ Flat(XYZ v, XYZ normal)
	{
		return v - normal * v.DotProduct(normal);
	}
}
