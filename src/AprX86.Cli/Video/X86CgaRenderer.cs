// X86CgaRenderer — walk the 0xB8000 80×25 text-mode framebuffer and
// emit a PNG. Mirrors the AprNes / AprGba `--screenshot=` convention
// exactly: pure data → packed RGB bytes → existing PNG encoder.
//
// Output: 640×350 PNG (80×8 columns by 25×14 rows = standard CGA text
// mode geometry; using the 8×14 glyphs Apr86 dumped, which is the
// classic IBM PC EGA/MDA proportion used by most BIOS POST screens).
//
// Attribute byte layout (CGA text mode):
//   bit 7   blink (we render as static — no blink animation)
//   bits 6:4  background (3-bit; 8 colors)
//   bit 3   foreground intensity
//   bits 2:0  foreground (3-bit base color)
//
// Effective: foreground uses full 16-color palette, background uses
// the low 8 colors only. Matches real CGA hardware default.

using System.IO;

namespace AprX86.Cli.Video;

public static class X86CgaRenderer
{
    public const int CellsW = 80;
    public const int CellsH = 25;
    public const int ImgW   = CellsW * X86CgaFont.FontW;     // 640
    public const int ImgH   = CellsH * X86CgaFont.FontH;     // 350

    /// <summary>16-colour CGA palette in 0xRRGGBB form.</summary>
    public static readonly uint[] Palette = new uint[]
    {
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA,
        0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF,
        0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    };

    // 30.7d — MDA-specific colours (IBM 5151 P39 green phosphor approx.
    // per Gemini 20260516_200703.txt). Real MDA hardware uses hardcoded
    // logic gates that pattern-match the attribute byte, not a 16-colour
    // palette. The bit positions of the byte are the same as CGA
    // (bit 7 = blink, bits 6-4 = bg, bit 3 = intensity, bits 2-0 = fg)
    // but only specific combinations are honoured.
    private const uint MdaBlack    = 0x000000;
    private const uint MdaDimGreen = 0x00A800;   // normal text
    private const uint MdaBright   = 0x54FC54;   // intensity bit 3 set

    /// <summary>
    /// Render the CGA text-mode framebuffer at 0xB8000 to a PNG file.
    /// Auto-detects MDA vs CGA: if 0xB8000 looks empty (mostly zero
    /// char bytes) but 0xB0000 has content, switches to MDA. Phase
    /// 29-supp — real PC/XT BIOS with MDA card writes to 0xB0000;
    /// without this we'd render a black PNG even though POST text is
    /// in memory.
    /// </summary>
    public static void Render(byte[] mem, string outPath)
    {
        int fbBase = PickFramebufferBase(mem);
        var rgb = RenderToRgbBytes(mem, fbBase);
        PngWriter.SavePng(rgb, ImgW, ImgH, outPath);
    }

    /// <summary>Sum of non-zero char bytes in a text-mode framebuffer.</summary>
    private static int FbContentScore(byte[] mem, int fbBase)
    {
        int score = 0;
        for (int i = 0; i < CellsW * CellsH; i++)
        {
            byte ch = mem[fbBase + i * 2];
            if (ch != 0x00 && ch != 0x20) score++;
        }
        return score;
    }

    /// <summary>
    /// Phase 29-supp — pick whichever of CGA / MDA framebuffer has
    /// printable content. Most machines map only one or the other.
    /// </summary>
    public static int PickFramebufferBase(byte[] mem)
    {
        int cga = FbContentScore(mem, 0xB8000);
        int mda = FbContentScore(mem, 0xB0000);
        return mda > cga ? 0xB0000 : 0xB8000;
    }

    /// <summary>
    /// Backward-compat overload — defaults to 0xB8000 (CGA). New
    /// callers should pass the framebuffer base explicitly when MDA
    /// auto-detect (above) doesn't apply.
    /// </summary>
    public static byte[] RenderToRgbBytes(byte[] mem) => RenderToRgbBytes(mem, 0xB8000);

    /// <summary>
    /// Render the text-mode framebuffer at <paramref name="fbBase"/>
    /// into a packed 24bpp RGB byte buffer (size <c>ImgW * ImgH * 3</c>).
    /// Used by both the PNG writer and the WinForms framebuffer blt
    /// path. fbBase = 0xB8000 for CGA, 0xB0000 for MDA.
    /// </summary>
    public static byte[] RenderToRgbBytes(byte[] mem, int fbBase)
    {
        var fontDir = X86CgaFont.LocateAprFontDir()
            ?? throw new FileNotFoundException(
                "could not locate OldProject/Apr86/Apr8086/ASII_FONT/. " +
                "Clone the Apr86 source repo into OldProject/Apr86 (it ships the 256 8×14 CGA glyph PNGs).");
        X86CgaFont.EnsureLoaded(fontDir);

        var rgb = new byte[ImgW * ImgH * 3];
        // 30.7d — MDA vs CGA attribute decode is fundamentally different:
        // real MDA hardware pattern-matches specific bit combinations to
        // hardcoded behaviours (invisible / underline / normal / reverse)
        // rather than indexing a colour palette. We detect MDA vs CGA
        // mode purely by framebuffer base address (BIOS chose 0xB0000
        // for MDA mode 7, 0xB8000 for CGA modes 0-6). Future: also
        // honour BDA[0x49] mode byte if the heuristic ever picks wrong.
        bool isMda = fbBase == 0xB0000;

        for (int cy = 0; cy < CellsH; cy++)
        for (int cx = 0; cx < CellsW; cx++)
        {
            int cellOff = fbBase + (cy * CellsW + cx) * 2;
            byte ch    = mem[cellOff];
            byte attr  = mem[cellOff + 1];
            uint fg, bg;
            if (isMda) (fg, bg) = MdaDecodeAttr(attr);
            else
            {
                fg = Palette[attr & 0x0F];
                bg = Palette[(attr >> 4) & 0x07];   // bit 7 = blink, ignored
            }
            DrawGlyph(rgb, ch, cx * X86CgaFont.FontW, cy * X86CgaFont.FontH, fg, bg);
        }

        return rgb;
    }

    /// <summary>
    /// Decode MDA text-mode attribute byte to (foreground, background)
    /// RGB pair. Real IBM MDA hardware ignores most CGA-style colour
    /// combos and only honours these four patterns (per IBM MDA tech
    /// ref + Gemini 20260516_200703.txt):
    ///   fg=000, bg=000           -> INVISIBLE (black on black)
    ///   fg=001, bg=000           -> UNDERLINE (we render as normal text;
    ///                                 underline glyph not yet rendered)
    ///   fg=000, bg=111           -> REVERSE VIDEO (black on green)
    ///   anything else            -> NORMAL (green on black)
    /// Intensity (bit 3) brightens the foreground in non-reverse cells.
    /// Blink (bit 7) ignored.
    /// </summary>
    private static (uint fg, uint bg) MdaDecodeAttr(byte attr)
    {
        int fgBits = attr & 0x07;
        int bgBits = (attr >> 4) & 0x07;
        bool intense = (attr & 0x08) != 0;
        if (fgBits == 0 && bgBits == 0)
            return (MdaBlack, MdaBlack);                       // invisible
        if (fgBits == 0 && bgBits == 7)
            return (MdaBlack, MdaDimGreen);                    // reverse video
        // Normal text (incl. underline path which we draw as plain text
        // for now). Intensity bit brightens the foreground.
        return (intense ? MdaBright : MdaDimGreen, MdaBlack);
    }

    private static void DrawGlyph(byte[] rgb, byte ch, int x0, int y0, uint fg, uint bg)
    {
        var bm = X86CgaFont.Bitmap;
        for (int y = 0; y < X86CgaFont.FontH; y++)
        {
            byte row = bm[ch * X86CgaFont.FontH + y];
            int rowStart = ((y0 + y) * ImgW + x0) * 3;
            for (int x = 0; x < X86CgaFont.FontW; x++)
            {
                bool on = (row & (1 << (7 - x))) != 0;
                uint c = on ? fg : bg;
                rgb[rowStart + x * 3 + 0] = (byte)(c >> 16);   // R
                rgb[rowStart + x * 3 + 1] = (byte)(c >> 8);    // G
                rgb[rowStart + x * 3 + 2] = (byte)c;           // B
            }
        }
    }
}
