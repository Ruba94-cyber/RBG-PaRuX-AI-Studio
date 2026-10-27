using System;
using System.Globalization;

namespace PaRuX.Services
{
    /// <summary>
    /// Wording, validation and AutomationIds for the Create > Edit image "Mask inpaint" mode. No WinUI types, so the
    /// rules (what blocks a run, what the user is told) are unit-tested and the view only binds them.
    /// </summary>
    public static class CreateInpaintPresenter
    {
        public const string ModeTag = "inpaint";
        public const string ModeItem = "Create_EditMode_Inpaint_Item";
        public const string Panel = "Create_Inpaint_Panel";
        public const string Canvas = "Create_Inpaint_Canvas";
        public const string BrushSlider = "Create_Inpaint_BrushSize_Slider";
        public const string PaintTool = "Create_Inpaint_Tool_Paint";
        public const string EraseTool = "Create_Inpaint_Tool_Erase";
        public const string InvertButton = "Create_Inpaint_Invert_Button";
        public const string ClearButton = "Create_Inpaint_Clear_Button";
        public const string GrowBox = "Create_Inpaint_Grow_NumberBox";
        public const string CoverageText = "Create_Inpaint_Coverage_Text";
        public const string ResultStrip = "Create_Inpaint_Result_Strip";
        public const string ResultImage = "Create_Inpaint_Result_Image";

        public const string ModeLabel = "Mask inpaint";

        public const string ModeDescription =
            "Paint over the part of the image to replace, then describe what should appear there. "
            + "Only the painted area is regenerated; the rest of the picture is kept. Large images are worked on at about one megapixel.";

        /// <summary>Share of the image above which the mask is treated as "everything": a whole-image edit is the honest tool then.</summary>
        public const double WholeImageFraction = 0.98;

        public static string Coverage(bool hasSource, double fraction)
        {
            if (!hasSource) return "Choose a source image, then paint over the area to replace.";
            if (fraction <= 0) return "Nothing painted yet. Paint over the area you want to replace.";
            return "Repainting " + (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "% of the image.";
        }

        /// <summary>The reason a run must not start, or null when it can.</summary>
        public static string? BlockingReason(bool hasSource, bool promptEmpty, double fraction)
        {
            if (!hasSource) return "Choose a source image first.";
            if (fraction <= 0) return "Paint over the area to replace first. The mask is empty.";
            if (fraction >= WholeImageFraction)
                return "The mask covers the whole image. Use Instruction edit or Image variation to change everything, or erase part of the mask.";
            if (promptEmpty) return "Describe what should appear in the painted area.";
            return null;
        }

        public static string BrushLabel(double percent) =>
            "Brush size " + Math.Clamp((int)Math.Round(percent), 1, 100).ToString(CultureInfo.InvariantCulture) + "%";

        public static string ResultCaption(string path) => "Inpainted result: " + System.IO.Path.GetFileName(path);
    }
}
