using System.Collections.Generic;

namespace ArtworkPlacer;

public class DialogSettings
{
	public string Collection;

	public string Filter = "art";

	public HeightAlign Align;

	public string HeightText;

	public string[] HeightDefaults;

	public string ClearText;

	public SideMode Side;

	public bool Multiple = true;

	public string GapText;

	public string MaxPerFaceText = "3";

	public bool Preview = true;

	public HashSet<long> LastChecked = new HashSet<long>();

	public bool ByRooms = true;

	public string Level;

	public HashSet<long> LastRooms = new HashSet<long>();
}
