using System.Collections.Generic;

namespace ArtworkPlacer;

public class TvSettings
{
	public string Filter = "tv";

	public bool AutoSize = true;

	public string FactorText = "4.5";

	public bool UseBottom = true;

	public string BottomText;

	public string ClearText;

	public bool Preview = true;

	public HashSet<long> LastChecked = new HashSet<long>();
}
