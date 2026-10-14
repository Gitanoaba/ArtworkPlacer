using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal static class ArtGeometry
{
	private static readonly Options GeomOptions = new Options
	{
		DetailLevel = ViewDetailLevel.Fine,
		IncludeNonVisibleObjects = false
	};

	public static List<XYZ> VisiblePoints(Element e)
	{
		List<XYZ> list = new List<XYZ>();
		GeometryElement geometryElement = e?.get_Geometry(GeomOptions);
		if (geometryElement != null)
		{
			Collect(geometryElement, Transform.Identity, list);
		}
		return list;
	}

	private static void Collect(GeometryElement ge, Transform t, List<XYZ> pts)
	{
		foreach (GeometryObject item in ge)
		{
			if (!(item is GeometryInstance geometryInstance))
			{
				if (!(item is Solid solid))
				{
					if (!(item is Mesh mesh))
					{
						continue;
					}
					foreach (XYZ vertex in mesh.Vertices)
					{
						pts.Add(t.OfPoint(vertex));
					}
				}
				else if (solid.Faces.Size > 0 && (solid.Volume > 1E-09 || solid.SurfaceArea > 1E-09))
				{
					BoundingBoxXYZ boundingBox = solid.GetBoundingBox();
					for (int i = 0; i < 8; i++)
					{
						XYZ point = new XYZ(((i & 1) == 0) ? boundingBox.Min.X : boundingBox.Max.X, ((i & 2) == 0) ? boundingBox.Min.Y : boundingBox.Max.Y, ((i & 4) == 0) ? boundingBox.Min.Z : boundingBox.Max.Z);
						pts.Add(t.OfPoint(boundingBox.Transform.OfPoint(point)));
					}
				}
			}
			else
			{
				Collect(geometryInstance.GetSymbolGeometry(), t.Multiply(geometryInstance.Transform), pts);
			}
		}
	}

	public static bool ZRange(Element e, out double z0, out double z1)
	{
		List<XYZ> list = VisiblePoints(e);
		if (list.Count > 0)
		{
			z0 = list.Min((XYZ p) => p.Z);
			z1 = list.Max((XYZ p) => p.Z);
			return true;
		}
		BoundingBoxXYZ boundingBoxXYZ = e?.get_BoundingBox((View)null);
		z0 = boundingBoxXYZ?.Min.Z ?? 0.0;
		z1 = boundingBoxXYZ?.Max.Z ?? 0.0;
		return boundingBoxXYZ != null;
	}

	public static double AlignDelta(HeightAlign align, double targetZ, double z0, double z1)
	{
		return align switch
		{
			HeightAlign.Bottom => targetZ - z0, 
			HeightAlign.Top => targetZ - z1, 
			_ => targetZ - (z0 + z1) / 2.0, 
		};
	}

	public static Level ReferenceLevel(Document doc, FamilyInstance fi)
	{
		if (fi.Host is Wall wall && doc.GetElement(wall.LevelId) is Level result)
		{
			return result;
		}
		if (doc.GetElement(fi.LevelId) is Level result2)
		{
			return result2;
		}
		ElementId elementId = ((Element)fi).get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsElementId();
		if (!(elementId != null))
		{
			return null;
		}
		return doc.GetElement(elementId) as Level;
	}
}
