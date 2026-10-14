using System;
using System.Collections.Generic;

namespace ArtworkPlacer;

internal class TvTarget
{
	public string Label;

	public List<TvSpot> Spots = new List<TvSpot>();

	public int Current = -1;

	public int LastOn;

	public string Note;

	public TvSpot Chosen
	{
		get
		{
			if (Current < 0 || Current >= Spots.Count)
			{
				return null;
			}
			return Spots[Current];
		}
	}

	public void Toggle()
	{
		if (Current >= 0)
		{
			LastOn = Current;
			Current = -1;
			Note = Note ?? "switched off in the preview";
		}
		else if (Spots.Count > 0)
		{
			Current = Math.Min(LastOn, Spots.Count - 1);
		}
	}
}
