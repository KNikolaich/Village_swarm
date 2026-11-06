// The book reader of the CrowPanel 3.7" (docs/reader.md): books from the TF card (txt, md, epub), no Wi-Fi, no timer. MENU held 2 s
// in the monitor brings it up; EXIT held 2 s anywhere in it goes back to the hive's tabs. The panel sleeps between the buttons (a few
// seconds awake after the last one, for quick paging), the e-paper keeps the page.
//
// Every book is first turned into one plain UTF-8 text (hornet-core/Reader.h) kept on the card in /.reader/NAME.EXT.txt with its
// note NAME.EXT.inf (what it was made from); the reading place is the byte offset of the page's first character in that text, kept
// in /.reader/NAME.EXT.pos at every page turn. A new font, size or orientation lays the page out again from the same offset.
//
// Buttons: browser - up/down = row, MENU or the rocker's press = open, EXIT = up a folder; book - rocker down (or up, by the
// setting "Качелька") or MENU = next page, the other way = page back, the rocker's press = settings, EXIT = close the book;
// settings - up/down = row, the press or MENU = change, EXIT = back to the book. EXIT held 2 s: back to the monitor.
//
// TF card pins from Elecrow's 3.7" TF example (github.com/Elecrow-RD/CrowPanel-ESP32-3.7-E-paper-HMI-Display-with-240-416): SPI on
// SCK 39, MISO 13, MOSI 40, CS 10, powered by GPIO42 high. The e-paper's own pins (11, 12, 45-48) are bit-banged: no clash.

#include <Arduino.h>
#include <FS.h>
#include <Preferences.h>
#include <SD.h>
#include <SPI.h>

#include <memory>

#include "../../lib/hornet-core/src/Invert.h"
#include "../../lib/hornet-core/src/Reader.h"
#include "crowpanel37.h"
#include "miniz.h"  // the ROM's inflate (tinfl)

namespace crowpanel37 {
namespace {

using namespace hornet::reader;

constexpr int kSdSck = 39, kSdMiso = 13, kSdMosi = 40, kSdCs = 10;
constexpr uint32_t kSdHz = 20000000;
constexpr uint32_t kAwakeMs = 5000;           // after the last button: quick paging needs no wake-up, then deep sleep
constexpr uint32_t kWindowBytes = 12 * 1024;  // the text read for a page (a page of the smallest font is ~2.5 KiB)
constexpr uint32_t kBackBytes = 8 * 1024;     // the text read before a page to find the one before it
constexpr size_t kMaxLine = 16 * 1024;        // a txt line longer than this is cut (no book should have one)
constexpr size_t kMaxPackage = 128 * 1024;    // the largest container.xml or OPF read into memory
constexpr int kHeaderFontHeight = 12;
const char kDir[] = "/.reader";

// ------------------------------------------------------------------ fonts and settings

struct Size {
    const char* label;
    const uint8_t* font;
};
struct Family {
    const char* name;
    Size sizes[4];
    int count;
};
// The Cyrillic fonts of U8g2 (ASCII and Cyrillic only: Reader.h printable() replaces the rest).
const Family kFamilies[] = {
    {"Моноширинный", {{"13 узкий", u8g2_font_6x13_t_cyrillic}, {"13", u8g2_font_8x13_t_cyrillic}, {"15", u8g2_font_9x15_t_cyrillic}, {"20", u8g2_font_10x20_t_cyrillic}}, 4},
    {"Юникод", {{"16", u8g2_font_unifont_t_cyrillic}}, 1},
    {"Пропорциональный", {{"12", u8g2_font_cu12_t_cyrillic}}, 1},
    {"Крупный", {{"24", u8g2_font_inr24_t_cyrillic}, {"30", u8g2_font_inr30_t_cyrillic}}, 2},
};
constexpr int kFamilyCount = sizeof kFamilies / sizeof kFamilies[0];

// The panel turned: GFX rotation (1 = 90° clockwise) and its name.
const struct {
    int rotation;
    const char* name;
} kOrientations[] = {{0, "книжная"}, {3, "альбомная"}, {2, "книжная, перевёрнутая"}, {1, "альбомная, перевёрнутая"}};
constexpr int kOrientationCount = 4;

struct Look {
    int family = 0, size = 1, orientation = 0;
    bool swapRocker = false;

    void load() {
        Preferences p;
        p.begin("reader", true);
        family = constrain(p.getInt("family", 0), 0, kFamilyCount - 1);
        size = constrain(p.getInt("size", 1), 0, kFamilies[family].count - 1);
        orientation = constrain(p.getInt("orient", 0), 0, kOrientationCount - 1);
        swapRocker = p.getBool("swap", false);
        p.end();
    }
    void save() const {
        Preferences p;
        p.begin("reader", false);
        p.putInt("family", family);
        p.putInt("size", size);
        p.putInt("orient", orientation);
        p.putBool("swap", swapRocker);
        p.end();
    }
    const uint8_t* font() const { return kFamilies[family].sizes[size].font; }
} look;

// ------------------------------------------------------------------ state kept across deep sleep

enum class View : uint8_t { Browser, Book, Menu, Note };

RTC_DATA_ATTR bool active = false;     // reading (also in NVS: a power loss comes back to the reader)
RTC_DATA_ATTR bool rtcValid = false;   // the RTC holds the state below (lost with the power)
RTC_DATA_ATTR View view = View::Browser;
RTC_DATA_ATTR char dirPath[256] = "/";
RTC_DATA_ATTR int cursor = 0;
RTC_DATA_ATTR char bookPath[256] = "";
RTC_DATA_ATTR char bookTitle[160] = "";
RTC_DATA_ATTR uint32_t textSize = 0, sourceSize = 0, sourceTime = 0;
RTC_DATA_ATTR uint32_t pageStart = 0, pageNext = 0;
RTC_DATA_ATTR bool pageLimited = false;  // the page was found going back: it ends at pageNext, not where it would by itself
RTC_DATA_ATTR History<24> history;
RTC_DATA_ATTR int menuRow = 0;
RTC_DATA_ATTR int battery = -1;  // percent, -1 not known

void saveMode() {
    Preferences p;
    p.begin("reader", false);
    p.putBool("active", active);
    p.putString("book", bookPath);
    p.putString("dir", dirPath);
    p.end();
}

void restoreMode() {
    Preferences p;
    p.begin("reader", true);
    active = p.getBool("active", false);
    const String book = p.getString("book", "");
    const String dir = p.getString("dir", "/");
    p.end();
    strlcpy(bookPath, book.c_str(), sizeof bookPath);
    strlcpy(dirPath, dir.length() ? dir.c_str() : "/", sizeof dirPath);
    view = bookPath[0] ? View::Book : View::Browser;
    cursor = 0;
    textSize = 0;
    history.clear();
    rtcValid = true;
}

// ------------------------------------------------------------------ the card

SPIClass sdSpi(HSPI);
bool cardUp = false;

bool mountCard() {
    pinMode(kPinSdPower, OUTPUT);
    digitalWrite(kPinSdPower, HIGH);
    delay(30);  // the card wakes up
    sdSpi.begin(kSdSck, kSdMiso, kSdMosi, kSdCs);
    cardUp = SD.begin(kSdCs, sdSpi, kSdHz);
    if (!cardUp) Serial.println("[reader] no card");
    return cardUp;
}

void cardDown() {
    if (cardUp) SD.end();
    sdSpi.end();
    cardUp = false;
    for (int pin : {kSdSck, kSdMiso, kSdMosi, kSdCs}) pinMode(pin, INPUT);  // nothing feeds the unpowered card
    digitalWrite(kPinSdPower, LOW);
}

std::string notePath(const std::string& book, const char* ext) { return std::string(kDir) + "/" + baseName(book) + ext; }

bool readSmall(const std::string& path, std::string& out, size_t limit = 4096) {
    File f = SD.open(path.c_str(), FILE_READ);
    if (!f) return false;
    const size_t n = std::min<size_t>(f.size(), limit);
    out.resize(n);
    const bool ok = f.read(reinterpret_cast<uint8_t*>(&out[0]), n) == n;
    f.close();
    return ok;
}

/// A small note written whole: into a .tmp first, then put in place.
bool writeSmall(const std::string& path, const std::string& data) {
    SD.mkdir(kDir);
    const std::string tmp = path + ".tmp";
    File f = SD.open(tmp.c_str(), FILE_WRITE);
    if (!f) return false;
    const bool ok = f.write(reinterpret_cast<const uint8_t*>(data.data()), data.size()) == data.size();
    f.close();
    if (!ok) {
        SD.remove(tmp.c_str());
        return false;
    }
    SD.remove(path.c_str());
    return SD.rename(tmp.c_str(), path.c_str());
}

/// The reading place: rewritten in place at every page turn (same length every time), so a power loss cannot leave it empty.
void savePosition() {
    const std::string path = notePath(bookPath, ".pos");
    const std::string note = formatPosition({pageStart, textSize, sourceSize, sourceTime});
    File f = SD.open(path.c_str(), "r+");
    if (f && f.size() == note.size()) {
        f.write(reinterpret_cast<const uint8_t*>(note.data()), note.size());
        f.close();
        return;
    }
    if (f) f.close();
    writeSmall(path, note);
}

// ------------------------------------------------------------------ drawing

int rotation() { return kOrientations[look.orientation].rotation; }

void beginDrawing() {
    GFXcanvas1& c = canvas();
    c.setRotation(rotation());
    c.fillScreen(0);
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.begin(c);
    t.setFontMode(1);
    t.setForegroundColor(1);
    t.setBackgroundColor(0);
}

int measure(const char* s, size_t n) {
    const std::string p = printable(s, n);
    return text().getUTF8Width(p.c_str());
}

void drawText(int x, int y, const std::string& s) { text().drawUTF8(x, y, printable(s.data(), s.size()).c_str()); }

/// The header: one line of the smallest font, <left> cut to fit, <right> and the battery on the right, a thin line under it.
/// Returns where the space under it starts.
int drawHeader(const std::string& left, const std::string& right) {
    GFXcanvas1& c = canvas();
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.setFont(u8g2_font_6x12_t_cyrillic);
    const int base = kHeaderFontHeight - 3;
    int x = c.width() - 2;
    if (battery >= 0) {
        const std::string pct = std::to_string(battery) + "%";
        x -= measure(pct.data(), pct.size());
        drawText(x, base, pct);
        x -= 15;  // the battery: 12 x 7 with its nub
        c.drawRect(x, base - 7, 12, 7, 1);
        c.fillRect(x + 12, base - 5, 2, 3, 1);
        c.fillRect(x + 2, base - 5, std::max(1, 8 * battery / 100), 3, 1);
        x -= 6;
    }
    if (!right.empty()) {
        x -= measure(right.data(), right.size());
        drawText(x, base, right);
        x -= 8;
    }
    drawText(2, base, fitWidth(left, x - 2, measure));
    c.drawFastHLine(0, kHeaderFontHeight - 1, c.width(), 1);
    return kHeaderFontHeight + 1;
}

/// A screen saying something, in the reader's turn: a title, lines, and how to get out.
void showNote(const char* title, std::initializer_list<std::string> lines) {
    beginDrawing();
    int y = drawHeader("Читалка", "");
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.setFont(u8g2_font_10x20_t_cyrillic);
    y += 24;
    drawText(8, y, title);
    t.setFont(u8g2_font_8x13_t_cyrillic);
    y += 8;
    for (const std::string& line : lines) {
        y += 17;
        drawText(8, y, fitWidth(line, canvas().width() - 16, measure));
    }
    drawText(8, canvas().height() - 8, "EXIT 2 с: выйти из читалки");
    present(false);
    view = View::Note;
}

// ------------------------------------------------------------------ the browser

std::vector<Entry> entries;

void readFolder() {
    entries.clear();
    File dir = SD.open(dirPath);
    if (!dir || !dir.isDirectory()) {
        strlcpy(dirPath, "/", sizeof dirPath);
        dir = SD.open("/");
    }
    std::vector<Entry> all;
    if (dir) {
        for (int n = 0; n < 2000; n++) {
            bool folder = false;
            const String name = dir.getNextFileName(&folder);
            if (name.isEmpty()) break;
            all.push_back({baseName(name.c_str()), folder});
        }
        dir.close();
    }
    entries = listing(std::move(all));
    if (cursor >= static_cast<int>(entries.size())) cursor = entries.empty() ? 0 : static_cast<int>(entries.size()) - 1;
}

constexpr int kRowHeight = 18;

int browserRows() { return std::max(1, (canvas().height() - kHeaderFontHeight - 6) / kRowHeight); }

void showBrowser() {
    beginDrawing();  // sets the turn, so the rows below are counted for it
    readFolder();
    const int rows = browserRows();
    const int pages = std::max<int>(1, (static_cast<int>(entries.size()) + rows - 1) / rows);
    const int first = firstRow(cursor, rows);
    const std::string where = std::string(dirPath) == "/" ? std::string("Карта памяти") : baseName(dirPath);
    const int top = drawHeader(where, pages > 1 ? std::to_string(first / rows + 1) + "/" + std::to_string(pages) : std::string());
    GFXcanvas1& c = canvas();
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.setFont(u8g2_font_9x15_t_cyrillic);
    if (entries.empty()) {
        drawText(8, top + 24, "Здесь нет книг");
        t.setFont(u8g2_font_8x13_t_cyrillic);
        drawText(8, top + 46, "Читаются txt, epub и md.");
        drawText(8, top + 64, std::string(dirPath) == "/" ? "Положите книги на карту." : "EXIT: назад");
    }
    for (int i = first; i < first + rows && i < static_cast<int>(entries.size()); i++) {
        const int y = top + 2 + (i - first) * kRowHeight;
        const std::string name = entries[i].folder ? entries[i].name + "/" : entries[i].name;
        drawText(6, y + 14, fitWidth(name, c.width() - 12, measure));  // every row alike: the same font and ink
        if (i == cursor) invertRect(c, 0, y, c.width(), kRowHeight, c.width(), c.height());  // the chosen one is then flipped: black bar, white text
    }
    present(false);
    view = View::Browser;
}

void openFolder(const std::string& path) {
    strlcpy(dirPath, path.c_str(), sizeof dirPath);
    cursor = 0;
    saveMode();
    showBrowser();
}

/// Up a folder, the cursor on the one we came from.
void folderUp() {
    const std::string from = baseName(dirPath);
    strlcpy(dirPath, parentOf(dirPath).c_str(), sizeof dirPath);
    readFolder();
    cursor = 0;
    for (size_t i = 0; i < entries.size(); i++)
        if (entries[i].name == from) cursor = static_cast<int>(i);
    saveMode();
    showBrowser();
}

// ------------------------------------------------------------------ building a book's text

/// Shows "Подготовка книги N%" now and then (each refresh of the e-paper takes a moment).
class Progress {
public:
    explicit Progress(const std::string& title) : title_(title) {}
    void at(uint64_t done, uint64_t total) {
        const int percent = total ? static_cast<int>(done * 100 / total) : 0;
        if (shown_ >= 0 && percent < shown_ + 25) return;
        shown_ = percent;
        showNote("Подготовка книги", {title_, std::to_string(percent) + "%", "", "Один раз для каждой книги."});
    }

private:
    std::string title_;
    int shown_ = -1;
};

/// txt and md: decoded (the encoding found from the first 32 KiB), split into lines, into the text.
bool convertLines(File& src, Format format, TextWriter& out, Progress& progress) {
    const uint32_t size = src.size();
    std::vector<uint8_t> buf(32 * 1024);
    const size_t sampled = src.read(buf.data(), buf.size());
    const Detected detected = detectEncoding(buf.data(), sampled);
    bool wrapped = false;
    if (format == Format::Txt) {
        std::string sample;
        Decoder d(detected.encoding);
        d.feed(buf.data() + detected.bom, sampled - detected.bom, [&](uint32_t cp) { appendUtf8(sample, cp); });
        wrapped = looksWrapped(lineLengths(sample));
    }
    buf.resize(4096);
    buf.shrink_to_fit();
    PlainText plain(out, wrapped);
    MarkdownText markdown(out);
    std::string line;
    auto endLine = [&] {
        if (format == Format::Md) markdown.line(line);
        else plain.line(line);
        line.clear();
    };
    auto emit = [&](uint32_t cp) {
        if (cp == '\n') endLine();
        else if (cp != '\r') appendUtf8(line, cp);
        if (line.size() > kMaxLine) endLine();
    };
    Decoder decoder(detected.encoding);
    if (!src.seek(detected.bom)) return false;
    uint32_t done = detected.bom;
    for (;;) {
        const int n = src.read(buf.data(), buf.size());
        if (n <= 0) break;
        decoder.feed(buf.data(), static_cast<size_t>(n), emit);
        done += n;
        progress.at(done, size);
    }
    decoder.finish(emit);
    if (!line.empty()) endLine();
    if (format == Format::Md) markdown.finish();
    else plain.finish();
    return true;
}

/// An entry of the zip, unpacked piece by piece into <sink> (stored or deflated; never whole in memory).
bool unzipEntry(const ReadAt& read, const ZipEntry& e, const std::function<void(const uint8_t*, size_t)>& sink) {
    uint32_t at = zipDataOffset(read, e);
    if (!at) return false;
    std::vector<uint8_t> in(4096);
    uint32_t left = e.compressedSize;
    if (e.method == 0) {
        while (left) {
            const uint32_t n = std::min<uint32_t>(left, in.size());
            if (!read(at, in.data(), n)) return false;
            sink(in.data(), n);
            at += n;
            left -= n;
        }
        return true;
    }
    if (e.method != 8) return false;
    std::unique_ptr<tinfl_decompressor> inflator(new (std::nothrow) tinfl_decompressor);
    std::unique_ptr<uint8_t[]> dict(new (std::nothrow) uint8_t[TINFL_LZ_DICT_SIZE]);
    if (!inflator || !dict) return false;
    tinfl_init(inflator.get());
    size_t inPos = 0, inLen = 0, outPos = 0;
    for (;;) {
        if (inPos == inLen && left) {
            inLen = std::min<uint32_t>(left, in.size());
            if (!read(at, in.data(), inLen)) return false;
            at += inLen;
            left -= inLen;
            inPos = 0;
        }
        size_t inBytes = inLen - inPos, outBytes = TINFL_LZ_DICT_SIZE - outPos;
        const tinfl_status status = tinfl_decompress(inflator.get(), in.data() + inPos, &inBytes, dict.get(), dict.get() + outPos, &outBytes,
                                                     left ? TINFL_FLAG_HAS_MORE_INPUT : 0);
        inPos += inBytes;
        if (outBytes) sink(dict.get() + outPos, outBytes);
        outPos = (outPos + outBytes) & (TINFL_LZ_DICT_SIZE - 1);
        if (status == TINFL_STATUS_DONE) return true;
        if (status < 0) return false;
        if (status == TINFL_STATUS_NEEDS_MORE_INPUT && inPos == inLen && !left) return false;  // cut short
    }
}

bool unzipToString(const ReadAt& read, const std::vector<ZipEntry>& entries, const std::string& name, std::string& out) {
    const ZipEntry* e = findEntry(entries, name);
    if (!e || e->size > kMaxPackage) return false;
    out.clear();
    out.reserve(e->size);
    return unzipEntry(read, *e, [&](const uint8_t* p, size_t n) { out.append(reinterpret_cast<const char*>(p), n); });
}

/// epub: the chapters in the spine's order, their markup stripped, into the text. <why> says what is wrong when it fails.
bool convertEpub(File& src, TextWriter& out, Progress& progress, std::string& title, std::string& why) {
    const ReadAt read = [&](uint32_t offset, uint8_t* into, size_t size) { return src.seek(offset) && src.read(into, size) == size; };
    const std::vector<ZipEntry> entries = zipDirectory(read, src.size());
    if (entries.empty()) {
        why = "Это не epub (не zip-архив).";
        return false;
    }
    std::string xml;
    if (!unzipToString(read, entries, "META-INF/container.xml", xml)) {
        why = "Нет META-INF/container.xml.";
        return false;
    }
    const std::string opfPath = packagePath(xml);
    if (opfPath.empty() || !unzipToString(read, entries, opfPath, xml)) {
        why = "Не читается описание книги (OPF).";
        return false;
    }
    const Package package = readPackage(xml, opfPath);
    xml.clear();
    xml.shrink_to_fit();
    if (package.spine.empty()) {
        why = "В книге нет глав.";
        return false;
    }
    title = package.title;
    for (size_t i = 0; i < package.spine.size(); i++) {
        const ZipEntry* e = findEntry(entries, package.spine[i]);
        if (!e) continue;  // a missing chapter: the rest is still worth reading
        MarkupText markup(out);
        Decoder decoder(Encoding::Utf8);
        auto put = [&](uint32_t cp) { markup.put(cp); };
        if (!unzipEntry(read, *e, [&](const uint8_t* p, size_t n) { decoder.feed(p, n, put); })) {
            why = "Глава повреждена: " + baseName(e->name);
            return false;
        }
        decoder.finish(put);
        markup.finish();
        out.blank();
        progress.at(i + 1, package.spine.size());
    }
    return true;
}

/// Makes the book's text and its note on the card. False: <why> says why.
bool buildText(const std::string& path, Format format, CacheInfo& info, std::string& why) {
    SD.mkdir(kDir);
    File src = SD.open(path.c_str(), FILE_READ);
    if (!src) {
        why = "Файл не открывается.";
        return false;
    }
    const std::string textPath = notePath(path, ".txt");
    const std::string tmp = textPath + ".tmp";
    File dst = SD.open(tmp.c_str(), FILE_WRITE);
    if (!dst) {
        src.close();
        why = "На карту не записать (защита от записи?).";
        return false;
    }
    bool written = true;
    TextWriter out([&](const char* s, size_t n) { written = written && dst.write(reinterpret_cast<const uint8_t*>(s), n) == n; });
    Progress progress(stem(path));
    progress.at(0, 1);
    std::string title;
    const bool ok = format == Format::Epub ? convertEpub(src, out, progress, title, why) : convertLines(src, format, out, progress);
    out.flush();
    src.close();
    dst.close();
    if (!ok || !written) {
        SD.remove(tmp.c_str());
        if (why.empty()) why = written ? "Книга не читается." : "Карта заполнена?";
        return false;
    }
    SD.remove(textPath.c_str());
    if (!SD.rename(tmp.c_str(), textPath.c_str())) {
        why = "На карту не записать.";
        return false;
    }
    info = CacheInfo{};
    info.sourceSize = sourceSize;
    info.sourceTime = sourceTime;
    info.textSize = static_cast<uint32_t>(out.size());
    info.title = title.empty() ? stem(path) : title;
    writeSmall(notePath(path, ".inf"), formatCacheInfo(info));
    return true;
}

// ------------------------------------------------------------------ the book

File textFile;
std::string window;     // the text read for the page last laid out
uint32_t windowAt = 0;  // its offset in the book's text

bool openText() {
    if (textFile) return true;
    textFile = SD.open(notePath(bookPath, ".txt").c_str(), FILE_READ);
    return static_cast<bool>(textFile);
}

bool readText(uint32_t from, uint32_t to, std::string& out) {
    out.resize(to - from);
    if (to == from) return true;
    return openText() && textFile.seek(from) && textFile.read(reinterpret_cast<uint8_t*>(&out[0]), to - from) == to - from;
}

/// The text area of the page in the chosen font and turn (the font is set on the drawing).
PageBox pageBox() {
    canvas().setRotation(rotation());
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.setFont(look.font());
    PageBox box;
    box.width = canvas().width() - 8;
    box.height = canvas().height() - kHeaderFontHeight - 4;
    box.lineHeight = t.getFontAscent() - t.getFontDescent() + 2;
    box.indent = measure("  ", 2);
    return box;
}

Page pageAt(uint32_t start, uint32_t limit = 0) {
    const uint32_t from = start > 0 ? start - 1 : 0;  // a byte before: whether a paragraph starts there
    const uint32_t to = std::min(textSize, start + kWindowBytes);
    windowAt = from;
    if (!readText(from, to, window)) return Page{start, start, {}};
    return layoutPage(TextWindow{window, from, textSize}, start, pageBox(), measure, limit);
}

Page pageBack(uint32_t current) {
    const uint32_t from = current > kBackBytes ? current - kBackBytes : 0;
    windowAt = from;
    if (!readText(from, current, window)) return Page{current, current, {}};
    const uint32_t perPage = std::max<uint32_t>(256, pageNext > pageStart ? pageNext - pageStart : 0);
    const uint32_t anchor = anchorBefore(window, from, current, std::min<uint32_t>(2 * perPage, kBackBytes / 2), kBackBytes - 1);
    return pageBefore(TextWindow{window, from, textSize}, anchor, current, pageBox(), measure);
}

void drawPage(const Page& page) {
    beginDrawing();
    const int top = drawHeader(bookTitle, std::to_string(percentOf(page.next, textSize)) + "%");
    const PageBox box = pageBox();  // sets the book's font
    const int ascent = text().getFontAscent();
    for (size_t i = 0; i < page.lines.size(); i++) {
        const Line& l = page.lines[i];
        if (l.end <= l.start || l.start < windowAt || l.end - windowAt > window.size()) continue;
        const std::string line = window.substr(l.start - windowAt, l.end - l.start);
        drawText(4 + (l.indent ? box.indent : 0), top + 2 + ascent + static_cast<int>(i) * box.lineHeight, line);
    }
    present(false);
    view = View::Book;
}

/// The page from pageStart as it is now (after a wake-up, a new font or turn).
void showPage() {
    const Page page = pageAt(pageStart, pageLimited ? pageNext : 0);
    pageNext = page.next;
    drawPage(page);
}

void forward(int pages) {
    Page page;
    bool moved = false;
    for (int i = 0; i < pages && pageNext < textSize && pageNext > pageStart; i++) {
        history.push(pageStart);
        pageStart = pageNext;
        page = pageAt(pageStart);
        pageNext = page.next;
        pageLimited = false;
        moved = true;
    }
    if (!moved) return;  // the last page: nothing to turn
    drawPage(page);
    savePosition();
}

void back(int pages) {
    Page page;
    bool moved = false;
    for (int i = 0; i < pages && pageStart > 0; i++) {
        uint32_t start = 0;
        if (history.pop(start) && start < pageStart) {
            page = pageAt(start, pageStart);
            if (page.next != pageStart) page = pageBack(pageStart);  // laid out otherwise since: find it anew
        } else {
            history.clear();
            page = pageBack(pageStart);
        }
        if (page.start >= pageStart) break;
        pageStart = page.start;
        pageNext = page.next;
        pageLimited = true;
        moved = true;
    }
    if (!moved) return;
    drawPage(page);
    savePosition();
}

/// Opens a book at its saved place, making its text first when needed.
void openBook(const std::string& path) {
    if (textFile) textFile.close();
    const Format format = formatOf(path);
    File src = SD.open(path.c_str(), FILE_READ);
    if (!src || src.isDirectory()) {
        if (src) src.close();
        bookPath[0] = 0;
        saveMode();
        showNote("Книги нет", {baseName(path), "Её убрали с карты?", "", "EXIT: к списку"});
        return;
    }
    sourceSize = src.size();
    sourceTime = static_cast<uint32_t>(src.getLastWrite());
    src.close();
    if (format == Format::None) {
        showNote("Не книга", {baseName(path), "Читаются txt, epub и md.", "", "EXIT: к списку"});
        return;
    }
    strlcpy(bookPath, path.c_str(), sizeof bookPath);
    CacheInfo info;
    std::string note;
    const bool cached = readSmall(notePath(path, ".inf"), note) && parseCacheInfo(note, info) && cacheFits(info, sourceSize, sourceTime) &&
                        SD.open(notePath(path, ".txt").c_str(), FILE_READ).size() == info.textSize;
    if (!cached) {
        std::string why;
        if (!buildText(path, format, info, why)) {
            bookPath[0] = 0;
            saveMode();
            showNote("Книга не читается", {baseName(path), why, "", "EXIT: к списку"});
            return;
        }
    }
    if (info.textSize == 0) {
        bookPath[0] = 0;
        saveMode();
        showNote("Пустая книга", {baseName(path), "В ней нет текста.", "", "EXIT: к списку"});
        return;
    }
    textSize = info.textSize;
    strlcpy(bookTitle, info.title.c_str(), sizeof bookTitle);
    Position position;
    pageStart = readSmall(notePath(path, ".pos"), note) && parsePosition(note, position) ? placeIn(position, info) : 0;
    pageLimited = false;
    history.clear();
    saveMode();
    showPage();
    savePosition();
}

/// Back from the book to its folder, the cursor on it.
void closeBook() {
    if (textFile) textFile.close();
    const std::string path = bookPath;
    bookPath[0] = 0;
    textSize = 0;
    strlcpy(dirPath, parentOf(path).c_str(), sizeof dirPath);
    readFolder();
    cursor = 0;
    for (size_t i = 0; i < entries.size(); i++)
        if (entries[i].name == baseName(path)) cursor = static_cast<int>(i);
    saveMode();
    showBrowser();
}

/// The book is on the screen and known (after a wake-up its text is checked again: the card may have changed meanwhile).
bool bookReady() {
    if (bookPath[0] && textSize && openText() && textFile.size() == textSize) return true;
    if (bookPath[0]) openBook(bookPath);  // shows it again (or says what is wrong)
    else showBrowser();
    return false;
}

// ------------------------------------------------------------------ the settings

enum MenuRow { RowFont, RowSize, RowOrientation, RowRocker, RowBack, RowCount };

void showMenu() {
    beginDrawing();
    const int top = drawHeader("Настройки читалки", "");
    GFXcanvas1& c = canvas();
    U8G2_FOR_ADAFRUIT_GFX& t = text();
    t.setFont(u8g2_font_8x13_t_cyrillic);
    const std::string rows[RowCount] = {
        std::string("Шрифт: ") + kFamilies[look.family].name,
        std::string("Размер: ") + kFamilies[look.family].sizes[look.size].label,
        std::string("Экран: ") + kOrientations[look.orientation].name,
        std::string("Качелька: ") + (look.swapRocker ? "наоборот" : "обычная"),
        "Назад к книге",
    };
    int y = top + 4;
    for (int i = 0; i < RowCount; i++, y += kRowHeight) {
        drawText(6, y + 14, fitWidth(rows[i], c.width() - 12, measure));
        if (i == menuRow) invertRect(c, 0, y, c.width(), kRowHeight, c.width(), c.height());
    }
    y += 8;
    drawText(6, y + 12, "Вверх/вниз: выбор,");
    drawText(6, y + 28, "нажатие или MENU: изменить,");
    drawText(6, y + 44, "EXIT: назад к книге.");
    c.drawFastHLine(0, y + 54, c.width(), 1);
    t.setFont(look.font());  // a sample of the font chosen
    drawText(6, y + 56 + t.getFontAscent() + 4, fitWidth("Съешь же ещё этих мягких французских булок", c.width() - 12, measure));
    present(false);
    view = View::Menu;
}

void changeMenuRow() {
    switch (menuRow) {
        case RowFont:
            look.family = (look.family + 1) % kFamilyCount;
            look.size = std::min(look.size, kFamilies[look.family].count - 1);
            break;
        case RowSize:
            look.size = (look.size + 1) % kFamilies[look.family].count;
            break;
        case RowOrientation:
            look.orientation = (look.orientation + 1) % kOrientationCount;
            wipeNext();  // everything moves
            break;
        case RowRocker:
            look.swapRocker = !look.swapRocker;
            break;
        default:
            return;
    }
    look.save();
    showMenu();
}

/// Back to the book after the settings: laid out anew from the same first character.
void leaveMenu() {
    pageLimited = false;
    history.clear();
    wipeNext();
    if (bookReady()) showPage();
}

// ------------------------------------------------------------------ buttons

enum class Event { None, Up, Down, Press, Menu, MenuLong, Exit, ExitLong };

/// Into the monitor: the reading place is kept already; the card goes off.
[[noreturn]] void leave() {
    Serial.println("[reader] off");
    if (textFile) textFile.close();
    active = false;
    saveMode();
    cardDown();
    backToMonitor();
}

void onEvent(Event e, int count = 1) {
    if (e == Event::ExitLong) leave();
    if (!cardUp && e != Event::None) {
        // no card when it woke: try again on any button
        if (!mountCard()) {
            showNote("Нет SD-карты", {"Вставьте карту с книгами", "(FAT32) и нажмите", "любую кнопку."});
            return;
        }
        if (bookPath[0]) openBook(bookPath);
        else showBrowser();
        return;
    }
    switch (view) {
        case View::Note:
            if (e == Event::Exit || e == Event::Press || e == Event::Menu) {
                if (bookPath[0]) openBook(bookPath);
                else showBrowser();
            }
            return;
        case View::Browser:
            if (e == Event::Up || e == Event::Down) {
                readFolder();
                cursor = stepRow(cursor, e == Event::Down ? count : -count, static_cast<int>(entries.size()));
                showBrowser();
            } else if (e == Event::Press || e == Event::Menu) {
                readFolder();
                if (entries.empty()) return;
                const Entry chosen = entries[cursor];
                const std::string path = joinPath(dirPath, chosen.name);
                if (chosen.folder) openFolder(path);
                else openBook(path);
            } else if (e == Event::Exit && std::string(dirPath) != "/") {
                folderUp();
            }
            return;
        case View::Book: {
            if (e == Event::Exit) {
                closeBook();
                return;
            }
            if (e == Event::Press) {
                menuRow = 0;
                showMenu();
                return;
            }
            if (!bookReady()) return;
            const bool forwardKey = e == Event::Menu || (e == (look.swapRocker ? Event::Up : Event::Down));
            const bool backKey = e == (look.swapRocker ? Event::Down : Event::Up);
            if (forwardKey) forward(count);
            else if (backKey) back(count);
            return;
        }
        case View::Menu:
            if (e == Event::Up || e == Event::Down) {
                menuRow = stepRow(menuRow, e == Event::Down ? count : -count, RowCount);
                showMenu();
            } else if (e == Event::Press || e == Event::Menu) {
                if (menuRow == RowBack) leaveMenu();
                else changeMenuRow();
            } else if (e == Event::Exit) {
                leaveMenu();
            }
            return;
    }
}

/// MENU or EXIT: a short press when let go, a long one at 2 s (while still held).
struct Hold {
    gpio_num_t pin;
    Event shortPress, longPress;
    uint32_t since = 0;
    bool fired = false;

    Event poll() {
        const bool down = pressed(pin);
        if (down && !since) since = millis() | 1;
        if (down && !fired && millis() - since >= 2000) {
            fired = true;
            return longPress;
        }
        if (!down && since) {
            const bool tap = !fired && millis() - since >= 30;  // bounce is no press
            since = 0;
            fired = false;
            if (tap) return shortPress;
        }
        return Event::None;
    }
};

/// Awake for a few seconds after the last button (paging on needs no wake-up), then deep sleep until the next.
[[noreturn]] void awake() {
    waitRelease();
    Hold menu{kPinMenu, Event::Menu, Event::MenuLong};
    Hold exit{kPinExit, Event::Exit, Event::ExitLong};
    uint32_t last = millis();
    for (;;) {
        Event e = Event::None;
        int count = 1;
        if (const int steps = takeSteps(); steps != 0) {
            e = steps > 0 ? Event::Down : Event::Up;
            count = std::abs(steps);
        } else if (takePress()) {
            e = Event::Press;
        } else if ((e = menu.poll()) == Event::None) {
            e = exit.poll();
        }
        if (e != Event::None) {
            onEvent(e, count);
            last = millis();
        }
        if (millis() - last > kAwakeMs && !menu.since && !exit.since) break;
        delay(10);
    }
    Serial.println("[reader] asleep until a button");
    if (textFile) textFile.close();
    cardDown();
    sleepUntilButton();
}

/// What the button that woke the panel means (MENU and EXIT: short or held).
Event wokenBy(uint64_t buttons) {
    if (buttons & (1ULL << kPinDown)) return Event::Down;
    if (buttons & (1ULL << kPinUp)) return Event::Up;
    if (buttons & (1ULL << kPinConf)) return Event::Press;
    if (buttons & (1ULL << kPinMenu)) return held(kPinMenu) ? Event::MenuLong : Event::Menu;
    if (buttons & (1ULL << kPinExit)) return held(kPinExit) ? Event::ExitLong : Event::Exit;
    return Event::None;
}

}  // namespace

bool readerOn() {
    if (!rtcValid) restoreMode();  // after a power loss: from NVS
    return active;
}

[[noreturn]] void runReader(uint64_t buttons, bool entering) {
    if (!rtcValid) restoreMode();
    look.load();
    watchButtons();
    battery = batteryPercent(batteryMv());
    const bool card = mountCard();
    if (entering) {
        Serial.println("[reader] on");
        active = true;
        saveMode();
        wipeNext();  // a new screen: no ghosts of the hive's tab
        if (!card) showNote("Нет SD-карты", {"Вставьте карту с книгами", "(FAT32) и нажмите", "любую кнопку."});
        else if (bookPath[0]) openBook(bookPath);
        else showBrowser();
        awake();
    }
    const Event e = wokenBy(buttons);
    Serial.printf("[reader] woken by 0x%llx\n", static_cast<unsigned long long>(buttons));
    if (e == Event::None) {
        // power-on or a reset while reading: the screen may hold anything, draw it anew
        wipeNext();
        if (!card) showNote("Нет SD-карты", {"Вставьте карту с книгами", "(FAT32) и нажмите", "любую кнопку."});
        else if (view == View::Book && bookPath[0]) openBook(bookPath);
        else if (view == View::Menu && bookPath[0] && bookReady()) showMenu();
        else showBrowser();
    } else if (!card) {
        cardUp = false;
        onEvent(e);  // says there is no card (or reads it, when it came meanwhile)
    } else {
        onEvent(e);
    }
    awake();
}

}  // namespace crowpanel37
