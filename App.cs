using System;
using System.IO;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ArtworkPlacer;

public class App : IExternalApplication
{
	private const string TabName = "Qbiq Tools";

	public Result OnStartup(UIControlledApplication app)
	{
		try
		{
			app.CreateRibbonTab("Qbiq Tools");
		}
		catch (Exception)
		{
		}
		AddButton(app.CreateRibbonPanel("Qbiq Tools", "Studio"), "RoomStudio", "Room\nStudio", "ArtworkPlacer.StudioCommand", "RoomStudio.png", "Plan TVs and artwork room by room on the floor plan, preview them, then place them.", "1. Click rooms on the plan, or pick them by name.\n2. Choose Artwork or TV for each. Meeting rooms start with a TV.\n3. Preview, adjust on the plan, then place everything in one step.");
		RibbonPanel panel = app.CreateRibbonPanel("Qbiq Tools", "Artwork");
		AddButton(panel, "PlaceArtwork", "Place\nArtwork", "ArtworkPlacer.PlaceArtworkCommand", "ArtworkPlacer.png", "Places artwork in the rooms you choose by name, or on the walls you select.", "1. Choose rooms by name (or select walls first to use those).\n2. Choose the artwork types: they rotate from wall to wall.\n3. Check the arrows on the plan and click any wall to switch the face it goes on.");
		AddButton(panel, "AlignArtworkHeights", "Align\nHeights", "ArtworkPlacer.AlignHeightsCommand", "AlignHeights.png", "Moves artwork that is already placed up or down so all pieces share the same height.", "Select the pieces to fix, or select nothing to align all the artwork in the active view.\nAlign by center, bottom edge or top edge, measured from each wall's level.");
		AddButton(app.CreateRibbonPanel("Qbiq Tools", "TV"), "PlaceTv", "Place\nTV", "ArtworkPlacer.PlaceTvCommand", "TvPlacer.png", "Places one TV per meeting room, on the short wall farthest from the door.", "1. Select the walls of one or more meeting rooms (or one wall per TV).\n2. Choose the TV types: bigger rooms get bigger TVs.\n3. Check the arrows and click another wall of a room to move its TV there.");
		return Result.Succeeded;
	}

	private static void AddButton(RibbonPanel panel, string name, string text, string className, string iconFile, string tooltip, string description)
	{
		PushButtonData pushButtonData = new PushButtonData(name, text, typeof(App).Assembly.Location, className)
		{
			ToolTip = tooltip,
			LongDescription = description
		};
		string text2 = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location), iconFile);
		if (File.Exists(text2))
		{
			BitmapImage image = (BitmapImage)(pushButtonData.LargeImage = new BitmapImage(new Uri(text2)));
			pushButtonData.Image = image;
		}
		panel.AddItem(pushButtonData);
	}

	public Result OnShutdown(UIControlledApplication app)
	{
		return Result.Succeeded;
	}
}
