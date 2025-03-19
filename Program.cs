using MaxRectsBinPack;
using SixLabors.ImageSharp.PixelFormats;
using SwfLib;
using SwfLib.Data;
using SwfLib.Shapes.FillStyles;
using SwfLib.Shapes.Records;
using SwfLib.Tags;
using SwfLib.Tags.BitmapTags;
using SwfLib.Tags.ControlTags;
using SwfLib.Tags.DisplayListTags;
using SwfLib.Tags.ShapeTags;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using CommandLine;

namespace SWFToPAM;

public enum ResourceFormat
{
    SEN,
    SPC
}

public class ProgramSettings
{
    [Option("exportImages", Required = false, HelpText = "If true, all images used in the SWF will be exported to a folder.")]
    public bool ExportImages { get; set; } = false;

    [Option("scaleImages", Required = true, HelpText = "If true, images used will be automatically setup for art scaling. (commonly known as 0.78125 scaling)")]
    public bool ScaleImages { get; set; }

    [Option("imageScale", Required = false, HelpText = "Scaling to use for images. The default of 0.78125 is what PopCap uses for PvZ2's animations.")]
    public float ImageScaleFactor { get; set; } = 0.78125f;

    [Option("resBasePath", Required = true, HelpText = "Sets the base path for image resources. Ex: zombie will result in the path images/full/zombie/{swf name} for SEN resources.")]
    public required string ResBasePath { get; set; }

    [Option("resFormat", Required = false, HelpText = "Format to generate resource info in. Available options are SEN. Defaults to SEN")]
    public ResourceFormat ResourceFormat { get; set; } = ResourceFormat.SEN;

    [Option("rsgName", Required = true, HelpText = "Name of the resource group this animation belongs to.")]
    public required string ResGroupName { get; set; }

    [Option("swf", Required = true, HelpText = "SWF file to convert into a PAM.")]
    public required string SWFFileName { get; set; }

    //[Option("log", Required = false, HelpText = "Logs additional information.")]
    //public bool LogOutput { get; set; } = false;
}

public class Program
{
    public static ProgramSettings Settings { get; private set; }

    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;

        Parser.Default.ParseArguments<ProgramSettings>(args)
            .WithParsed<ProgramSettings>(settings =>
            {
                Settings = settings;
                SwfToPam swfConverter = new SwfToPam();
                swfConverter.ParseSWF();
            });
        //ProgramSettings s = new() { ResourceFormat = ResourceFormat.SEN, ScaleImages = true, ExportImages = true, ResGroupName = "ZombieDarkWizardGroup", ResBasePath = "zombie", SWFFileName = "zombie_dark_wizard.swf", };
        //ProgramSettings s = new() { ResourceFormat = ResourceFormat.SEN, ScaleImages = false, ExportImages = true, ResGroupName = "ZombieDarkWizardGroup", ResBasePath = "zombie", SWFFileName = "zombie_dark_wizard.swf", };
        //Settings = s;
        //var p = new SwfToPam();
        //p.ParseSWF();
    }

    private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
    {
        Console.WriteLine("Unhandled exception occurred: " + e.ExceptionObject.ToString());
        Console.WriteLine("Press enter to continue...");
        Console.ReadLine();
        System.Environment.Exit(1337);
    }
}
