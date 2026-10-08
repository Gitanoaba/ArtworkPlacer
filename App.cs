using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ArtworkPlacer
{
    // ─── RIBBON PANEL (se carga al arrancar Revit) ───────────────────────────
    public class App : IExternalApplication
    {
        private const string TabName = "Qbiq Tools";

        public Result OnStartup(UIControlledApplication app)
        {
            // La pestaña puede existir ya si otro add-in nuestro la creó
            try { app.CreateRibbonTab(TabName); } catch (Exception) { }

            AddButton(app.CreateRibbonPanel(TabName, "Artwork"),
                "PlaceArtwork", "Place\nArtwork", "ArtworkPlacer.PlaceArtworkCommand", "ArtworkPlacer.png",
                "Automatically places an artwork piece centered on the longest free segment of each selected wall.",
                "1. Select the walls (or click the button and pick them).\n" +
                "2. Choose the artwork types: they rotate from wall to wall.\n" +
                "3. Check the arrows and click any wall to switch the face it goes on.");

            AddButton(app.CreateRibbonPanel(TabName, "TV"),
                "PlaceTv", "Place\nTV", "ArtworkPlacer.PlaceTvCommand", "TvPlacer.png",
                "Places one TV per meeting room, on the short wall farthest from the door.",
                "1. Select the walls of one or more meeting rooms (or one wall per TV).\n" +
                "2. Choose the TV types: bigger rooms get bigger TVs.\n" +
                "3. Check the arrows and click another wall of a room to move its TV there.");

            return Result.Succeeded;
        }

        private static void AddButton(RibbonPanel panel, string name, string text, string className, string iconFile, string tooltip, string description)
        {
            var buttonData = new PushButtonData(name, text, typeof(App).Assembly.Location, className)
            {
                ToolTip = tooltip,
                LongDescription = description
            };

            // Cargar ícono desde la misma carpeta que la DLL
            string iconPath = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location), iconFile);
            if (File.Exists(iconPath))
            {
                BitmapImage icon = new BitmapImage(new Uri(iconPath));
                buttonData.LargeImage = icon;
                buttonData.Image = icon;
            }

            panel.AddItem(buttonData);
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
