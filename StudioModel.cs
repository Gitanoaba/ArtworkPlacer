using System.Collections.Generic;
using System.Windows;

namespace ArtworkPlacer;

public enum RoomUse
{
	None,
	Artwork,
	Tv
}

public class PlanRoom
{
	public long Id;

	public string Name;

	public string Number;

	public string AreaText;

	public List<Point[]> Loops = new List<Point[]>();

	public Point Label;

	public RoomUse Use;

	public bool Selected;

	public string Problem;

	public string Collection;

	public ArtZone? Zone;

	public string Title => (Number.Length == 0) ? Name : (Name + " " + Number);
}

public class PlanWall
{
	public Point[] Path;

	public double Thickness;

	public bool Separator;
}

public class PlanDoor
{
	public Point Center;

	public Vector Along;

	public Vector Facing;

	public double Width;

	public double Thickness;
}

public class PlanLevel
{
	public long Id;

	public string Name;

	public List<PlanRoom> Rooms = new List<PlanRoom>();

	public List<PlanWall> Walls = new List<PlanWall>();

	public List<PlanDoor> Doors = new List<PlanDoor>();

	public List<Point[]> Cores = new List<Point[]>();

	public Point? Entrance;

	public Vector EntranceOut;
}

public class PlanMarker
{
	public int Key;

	public long LevelId;

	public long RoomId;

	public RoomUse Use;

	public Point Center;

	public Vector Along;

	public Vector Normal;

	public double Width;

	public string Label;

	public bool Skipped;

	public bool CanMove;

	public bool Manual;
}

public class PlanFace
{
	public int Key;

	public long LevelId;

	public long RoomId;

	public Point A;

	public Point B;

	public Vector Normal;

	public string Title;

	public string WallType;

	public bool Allowed;

	public string Reason;

	public bool Core;

	public bool TvWall;

	public bool Logo;

	public bool On;

	public bool Suggested;

	public string Problem;

	public Point? AddAt;
}

public class StudioRequest
{
	public Dictionary<long, RoomUse> Uses = new Dictionary<long, RoomUse>();

	public bool DrywallOnly = true;

	public bool ArtInOffices;

	public bool PlaceLogo;

	public Dictionary<long, string> RoomCollections = new Dictionary<long, string>();

	public string Client;

	public Dictionary<long, ArtZone> RoomZones = new Dictionary<long, ArtZone>();

	public List<long> ArtTypes = new List<long>();

	public List<long> TvTypes = new List<long>();

	public double? ArtHeight;

	public HeightAlign ArtAlign;

	public double Clearance;

	public int MaxPerFace;

	public double Gap;

	public double? TvBottom;

	public bool TvAutoSize;
}

public class PreviewResult
{
	public List<PlanMarker> Markers = new List<PlanMarker>();

	public List<PlanFace> Faces = new List<PlanFace>();

	public Dictionary<long, RoomUse> Uses = new Dictionary<long, RoomUse>();

	public Dictionary<long, string> Problems = new Dictionary<long, string>();

	public string Message;
}

public interface IStudioHost
{
	List<PlanLevel> Levels { get; }

	long StartLevel { get; }

	List<TypeItem> ArtTypes { get; }

	List<TypeItem> TvTypes { get; }

	List<ArtCollection> Collections { get; }

	List<ClientBoard> Boards { get; }

	string StartNote { get; }

	string LayoutNote { get; }

	string DesignFolder { get; }

	bool HasLogoWall { get; }

	Thumb Thumb(long typeId);

	double? ParseLength(string text);

	void LoadFamilies(string[] paths);

	PreviewResult Plan(StudioRequest request);

	PreviewResult SetRoomUse(IEnumerable<long> roomIds, RoomUse use);

	PreviewResult ChangeFace(int faceKey);

	PreviewResult MoveTv(long roomId);

	PreviewResult Suggest();

	PreviewResult SetRoomCollection(long roomId, string collection);

	PreviewResult SetRoomZone(long roomId, ArtZone? zone);

	PreviewResult MovePiece(int markerKey, Point center);

	PreviewResult AddPiece(int faceKey);

	PreviewResult RemovePiece(int markerKey);
}

public class StudioSettings
{
	public HashSet<long> ArtChecked = new HashSet<long>();

	public HashSet<long> TvChecked = new HashSet<long>();

	public Dictionary<long, RoomUse> Uses = new Dictionary<long, RoomUse>();

	public HeightAlign Align;

	public string HeightText;

	public string ClearText;

	public string GapText;

	public string MaxText = "2";

	public string TvBottomText;

	public bool TvAutoSize = true;

	public bool DrywallOnly = true;

	public bool ArtInOffices;

	public bool PlaceLogo = true;

	public string Collection;

	public Dictionary<long, string> RoomCollections = new Dictionary<long, string>();

	public string Client;

	public Dictionary<long, ArtZone> RoomZones = new Dictionary<long, ArtZone>();

	public Dictionary<string, string> Layouts = new Dictionary<string, string>();
}
