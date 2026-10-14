using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal class TvSpot
{
	public Wall Wall;

	public int Sign;

	public double U0;

	public double U1;

	public double? Depth;

	public bool Usable;

	public bool HasTv;

	public FamilySymbol Symbol;

	public double Width;

	public double Center;

	public string Problem;

	public double FaceV;

	public XYZ OnFace;

	public XYZ Normal;

	public XYZ Along;
}
