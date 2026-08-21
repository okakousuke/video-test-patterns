using System.Windows.Media.Imaging;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>生成画面で選ぶパターン。用途の見出しと小さな参考図を持ちます。</summary>
public sealed class PatternChoice
{
    public PatternChoice(string name)
    {
        Name = name;
        Category = CategoryOf(name);
    }

    public string Name { get; }
    public string Category { get; }
    public BitmapSource? Icon => PatternThumbnails.ForIcon(Name);

    public static string CategoryOf(string name) => name switch
    {
        "colorbar" or "colorbar75" or "grayramp" or "graysteps" or "gamma"
            or "colorramp" or "colormatrix" or "rainbow" or "stepmatrix"
            or "splitsteps" or "shallowramp" or "triangleramp" or "wedge"
            => "階調・色・レベル",

        "frame" or "crosshair" or "grid" or "circles" or "radial"
            or "square" or "window" or "geometrycard"
            => "画面・位置・幾何",

        "hatch" or "dots" or "blocks" or "multiburst" or "zoneplate"
            or "checker" or "pulsebar" or "sweep" or "siemens"
            or "linepairs" or "slantedge" or "raster"
            => "解像度・周波数",

        "smptebars" or "pluge" or "splitbars" or "testcard" or "barshd"
            or "resolutioncard" or "monoscope" or "digitalcard"
            => "放送・総合カード",

        _ => "特殊・その他",
    };
}
