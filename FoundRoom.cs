using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal class FoundRoom
{
	public ElementId LevelId;

	public List<XYZ> Polygon = new List<XYZ>();

	public List<RoomWall> Walls = new List<RoomWall>();

	public double Area;
}
