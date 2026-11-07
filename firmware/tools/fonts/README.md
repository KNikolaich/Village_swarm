# Fonts for the book reader

TTF files that `../make-reader-fonts.ps1` turns into reader fonts (see `../reader-fonts.json`, docs/reader.md). Only the TTF and the licence text are used.

| File | Font | Licence |
|---|---|---|
| `Literata-VF.ttf` | Literata (variable: weight and optical size; the script cuts regular 400) | SIL OFL 1.1, `OFL-Literata.txt` |
| `PT_Serif-Web-Regular.ttf` | PT Serif Regular | SIL OFL 1.1, `OFL-PTSerif.txt` (Reserved Font Names "PT Sans", "PT Serif", "ParaType") |
| `RobotoMono-VF.ttf` | Roboto Mono (variable weight; regular 400) | SIL OFL 1.1, `OFL-RobotoMono.txt` |

Source: github.com/google/fonts, `ofl/literata`, `ofl/ptserif`, `ofl/robotomono`. The fonts go into the firmware only as 1-bit bitmaps (arrays in `lib/reader-fonts/ReaderFonts.h`);
the licence texts stay here for that. All three have the whole range the reader draws: ASCII, Yo, A-ya, yo.
