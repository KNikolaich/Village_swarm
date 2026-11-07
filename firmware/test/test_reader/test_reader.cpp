#include <unity.h>

#include <cstring>
#include <string>
#include <vector>

#include "../../lib/hornet-core/src/Invert.h"
#include "../../lib/hornet-core/src/Reader.h"
#include "tiny_epub.h"

using namespace hornet::reader;

void setUp() {}
void tearDown() {}

// ------------------------------------------------------------------ helpers

static std::string decodeAll(Encoding encoding, const std::string& bytes, size_t chunk = 3) {
    std::string out;
    Decoder d(encoding);
    auto emit = [&](uint32_t cp) { appendUtf8(out, cp); };
    for (size_t i = 0; i < bytes.size(); i += chunk)
        d.feed(reinterpret_cast<const uint8_t*>(bytes.data()) + i, std::min(chunk, bytes.size() - i), emit);
    d.finish(emit);
    return out;
}

static std::string plain(const std::string& txt, bool wrapped = false) {
    std::string out;
    TextWriter w([&](const char* s, size_t n) { out.append(s, n); });
    PlainText p(w, wrapped);
    size_t start = 0;
    for (;;) {
        const size_t nl = txt.find('\n', start);
        p.line(txt.substr(start, nl == std::string::npos ? std::string::npos : nl - start));
        if (nl == std::string::npos) break;
        start = nl + 1;
    }
    p.finish();
    w.flush();
    return out;
}

static std::string markdown(const std::string& md) {
    std::string out;
    TextWriter w([&](const char* s, size_t n) { out.append(s, n); });
    MarkdownText m(w);
    size_t start = 0;
    for (;;) {
        const size_t nl = md.find('\n', start);
        m.line(md.substr(start, nl == std::string::npos ? std::string::npos : nl - start));
        if (nl == std::string::npos) break;
        start = nl + 1;
    }
    m.finish();
    w.flush();
    return out;
}

static std::string markup(const std::string& html) {
    std::string out;
    TextWriter w([&](const char* s, size_t n) { out.append(s, n); });
    MarkupText m(w);
    m.put(html);
    m.finish();
    w.flush();
    return out;
}

/// A fake monospace font: every character is 10 px wide.
static int mono(const char* s, size_t n) {
    int chars = 0;
    for (size_t i = 0; i < n;) {
        decodeUtf8(s, n, i);
        chars++;
    }
    return chars * 10;
}

// ------------------------------------------------------------------ decoding

static void test_utf8_passes_and_broken_bytes_become_one_mark_each() {
    TEST_ASSERT_EQUAL_STRING("Привет, мир", decodeAll(Encoding::Utf8, "Привет, мир").c_str());
    TEST_ASSERT_EQUAL_STRING("a\xEF\xBF\xBD" "b", decodeAll(Encoding::Utf8, "a\xFF" "b").c_str());
    TEST_ASSERT_EQUAL_STRING("a\xEF\xBF\xBD" "b", decodeAll(Encoding::Utf8, "a\xD0" "b").c_str());  // cut sequence
    TEST_ASSERT_EQUAL_STRING("x\xEF\xBF\xBD", decodeAll(Encoding::Utf8, "x\xD0").c_str());           // cut by the end
    TEST_ASSERT_EQUAL_STRING("\xEF\xBF\xBD\xEF\xBF\xBD", decodeAll(Encoding::Utf8, "\xC0\xAF", 1).c_str());  // overlong: two marks
    size_t i = 0;
    bool complete = true;
    TEST_ASSERT_EQUAL(0, decodeUtf8("\xD0", 1, i, &complete));
    TEST_ASSERT_FALSE(complete);
    TEST_ASSERT_EQUAL(0u, i);
}

static void test_encodings_are_told_apart() {
    const std::string utf8 = "Мастер и Маргарита";
    TEST_ASSERT_TRUE(detectEncoding(reinterpret_cast<const uint8_t*>(utf8.data()), utf8.size()).encoding == Encoding::Utf8);
    const std::string cp = "\xCC\xE0\xF1\xF2\xE5\xF0 \xE8 \xCC\xE0\xF0\xE3\xE0\xF0\xE8\xF2\xE0";  // the same in Windows-1251
    TEST_ASSERT_TRUE(detectEncoding(reinterpret_cast<const uint8_t*>(cp.data()), cp.size()).encoding == Encoding::Cp1251);
    TEST_ASSERT_EQUAL_STRING(utf8.c_str(), decodeAll(Encoding::Cp1251, cp).c_str());
    TEST_ASSERT_EQUAL_STRING("\xD0\x81\xD1\x91\xE2\x80\x94", decodeAll(Encoding::Cp1251, "\xA8\xB8\x97").c_str());  // Ё ё —
    const uint8_t bom8[] = {0xEF, 0xBB, 0xBF, 'a'};
    TEST_ASSERT_EQUAL(3u, detectEncoding(bom8, 4).bom);
    const std::string le("\xFF\xFE\x1F\x04\x40\x04", 6);  // BOM, "Пр"
    const Detected d = detectEncoding(reinterpret_cast<const uint8_t*>(le.data()), le.size());
    TEST_ASSERT_TRUE(d.encoding == Encoding::Utf16Le);
    TEST_ASSERT_EQUAL_STRING("Пр", decodeAll(Encoding::Utf16Le, le.substr(2)).c_str());
    const std::string be("\x04\x1F\x04\x40\xD8\x3D\xDE\x00", 8);  // "Пр" and a surrogate pair
    const std::string bare("\x00H\x00i\x00!\x00!", 8);  // UTF-16 BE without a BOM, told by its zero bytes
    TEST_ASSERT_TRUE(detectEncoding(reinterpret_cast<const uint8_t*>(bare.data()), bare.size()).encoding == Encoding::Utf16Be);
    TEST_ASSERT_EQUAL_STRING("Пр\xF0\x9F\x98\x80", decodeAll(Encoding::Utf16Be, be).c_str());
    TEST_ASSERT_TRUE(detectEncoding(reinterpret_cast<const uint8_t*>(""), 0).encoding == Encoding::Utf8);  // empty
}

// ------------------------------------------------------------------ txt

static void test_txt_lines_are_paragraphs_with_one_gap_at_most() {
    TEST_ASSERT_EQUAL_STRING("Первый абзац.\nВторой.\n\nТретий после пустых.\n", plain("  Первый   абзац.\r\nВторой.\r\n\r\n\r\n\r\n\tТретий после пустых.\r\n").c_str());
    TEST_ASSERT_EQUAL_STRING("", plain("").c_str());
    TEST_ASSERT_EQUAL_STRING("", plain("\n\n\n").c_str());
}

static void test_hand_wrapped_txt_is_joined_back() {
    std::string txt;
    std::vector<size_t> lengths;
    for (int p = 0; p < 6; p++) {
        for (int l = 0; l < 4; l++) txt += "This line of a paragraph is about sixty characters long ok.\n";
        txt += "End.\n\n";
    }
    TEST_ASSERT_TRUE(looksWrapped(lineLengths(txt)));
    TEST_ASSERT_FALSE(looksWrapped(lineLengths("Short.\nLines.\n")));
    std::string longLines;
    for (int i = 0; i < 30; i++) longLines += std::string(300, 'x') + "\n";
    TEST_ASSERT_FALSE(looksWrapped(lineLengths(longLines)));
    const std::string joined = plain(txt, true);
    TEST_ASSERT_EQUAL(6, static_cast<int>(std::count(joined.begin(), joined.end(), '\n')));
    TEST_ASSERT_EQUAL_STRING("A long line one here goes on.\nIndented new one.\n",
                             plain("A long line one here\ngoes on.\n   Indented new one.", true).c_str());
}

// ------------------------------------------------------------------ md

static void test_markdown_loses_its_marks() {
    TEST_ASSERT_EQUAL_STRING("Глава\nТекст с жирным, курсивом и code, ссылка и snake_case.\n",
                             markdown("# Глава #\nТекст с **жирным**, _курсивом_ и `code`,\n[ссылка](http://x.y) ![картинка](a.png) и snake_case.").c_str());
    TEST_ASSERT_EQUAL_STRING("\xE2\x80\xA2 один\n\xE2\x80\xA2 два\n1. три\nцитата\n", markdown("- один\n* два\n1. три\n> цитата").c_str());
    TEST_ASSERT_EQUAL_STRING("Имя / Возраст\nАня / 7\n", markdown("| Имя | Возраст |\n|---|:-:|\n| Аня | 7 |").c_str());
    TEST_ASSERT_EQUAL_STRING("code **as is**\nПосле\n", markdown("```\ncode **as is**\n```\nПосле").c_str());
    TEST_ASSERT_EQUAL_STRING("Заголовок\nабзац\n\nдальше\n", markdown("Заголовок\n=========\nабзац\n\n---\n\nдальше").c_str());
    TEST_ASSERT_EQUAL_STRING("строка\nновая\n", markdown("строка  \nновая").c_str());
    TEST_ASSERT_EQUAL_STRING("*звёздочка*\n", markdown("\\*звёздочка\\*").c_str());
}

// ------------------------------------------------------------------ markup

static void test_markup_becomes_paragraphs_and_entities_characters() {
    TEST_ASSERT_EQUAL_STRING("Глава\nНе разговаривайте\xE2\x80\xA6\nСтрока\nвторая.\n",
                             markup("<html><head><title>T</title><style>p{color:red}</style></head><body><h1>Глава</h1>\n"
                                    "<p>Не&nbsp;разговаривайте&#x2026;</p><p>Строка<br/>вторая<img src='a>b.png'/>.</p><!-- <p>no</p> --></body></html>")
                                 .c_str());
    TEST_ASSERT_EQUAL_STRING("a & b < c &unknown; d &\n", markup("<p>a &amp; b &lt; c &unknown; d &</p>").c_str());
    TEST_ASSERT_EQUAL_STRING("\xE2\x80\xA2 x\n\xE2\x80\xA2 y\n", markup("<ul><li>x</li><li>y</li></ul>").c_str());
    TEST_ASSERT_EQUAL_STRING("\xC2\xAB\xD0\x81\xC2\xBB\n", markup("<p>&laquo;&#1025;&raquo;</p>").c_str());
    TEST_ASSERT_EQUAL_STRING("", markup("").c_str());
    TEST_ASSERT_EQUAL_STRING("x\n", markup("<script>if (a<b) x();</script><p>x</p>").c_str());
}

// ------------------------------------------------------------------ zip and epub

static ReadAt fixture() {
    return [](uint32_t offset, uint8_t* into, size_t size) {
        if (offset + size > sizeof kTinyEpub) return false;
        std::memcpy(into, kTinyEpub + offset, size);
        return true;
    };
}

static std::string entryText(const std::vector<ZipEntry>& entries, const std::string& name) {
    const ZipEntry* e = findEntry(entries, name);
    if (!e) return "";
    const uint32_t at = zipDataOffset(fixture(), *e);
    std::string out(e->size, '\0');
    fixture()(at, reinterpret_cast<uint8_t*>(&out[0]), e->size);
    return out;
}

static void test_epub_package_gives_title_and_reading_order() {
    const auto entries = zipDirectory(fixture(), sizeof kTinyEpub);
    TEST_ASSERT_EQUAL(6, static_cast<int>(entries.size()));
    TEST_ASSERT_EQUAL(0, entries[0].method);
    const std::string opfPath = packagePath(entryText(entries, "META-INF/container.xml"));
    TEST_ASSERT_EQUAL_STRING("OEBPS/content.opf", opfPath.c_str());
    const Package p = readPackage(entryText(entries, opfPath), opfPath);
    TEST_ASSERT_EQUAL_STRING("Мастер & Маргарита", p.title.c_str());
    TEST_ASSERT_EQUAL(2, static_cast<int>(p.spine.size()));  // the image in the spine is left out
    TEST_ASSERT_EQUAL_STRING("OEBPS/text/ch1.xhtml", p.spine[0].c_str());
    TEST_ASSERT_EQUAL_STRING("OEBPS/text/ch 2.xhtml", p.spine[1].c_str());
    TEST_ASSERT_EQUAL_STRING("Глава первая\nНикогда не разговаривайте с неизвестными\xE2\x80\xA6\nСтрока\nвторая строка.\n",
                             markup(entryText(entries, p.spine[0])).c_str());
    TEST_ASSERT_NOT_NULL(findEntry(entries, "oebps/TEXT/ch1.xhtml"));  // case differs
}

static void test_not_a_zip_and_no_title() {
    const uint8_t junk[64] = {1, 2, 3};
    const ReadAt read = [&](uint32_t offset, uint8_t* into, size_t size) {
        if (offset + size > sizeof junk) return false;
        std::memcpy(into, junk + offset, size);
        return true;
    };
    TEST_ASSERT_EQUAL(0, static_cast<int>(zipDirectory(read, sizeof junk).size()));
    TEST_ASSERT_EQUAL(0, static_cast<int>(zipDirectory(read, 10).size()));
    const Package p = readPackage("<package><manifest/><spine/></package>", "x.opf");
    TEST_ASSERT_EQUAL_STRING("", p.title.c_str());
    TEST_ASSERT_EQUAL(0, static_cast<int>(p.spine.size()));
    TEST_ASSERT_EQUAL_STRING("", packagePath("<container/>").c_str());
    TEST_ASSERT_EQUAL_STRING("a/c.xhtml", resolvePath("a/b/x.opf", "../c.xhtml#frag").c_str());
    TEST_ASSERT_EQUAL_STRING("c.xhtml", resolvePath("x.opf", "./c.xhtml").c_str());
}

// ------------------------------------------------------------------ layout

static std::string book() {
    std::string text;
    for (int p = 0; p < 40; p++) {
        for (int w = 0; w < 25 + p % 7; w++) text += (w ? " " : "") + std::string(w % 3 == 0 ? "слово" : "ах") + std::to_string(p);
        text += p % 9 == 4 ? "\n\n" : "\n";
    }
    return text;
}

static std::string lineText(const std::string& text, const Line& l) { return text.substr(l.start, l.end - l.start); }

static void test_lines_fit_and_long_words_break() {
    const std::string text = "Короткие слова и сверхдлиннющееслово тут\n";
    const TextWindow w{text, 0, static_cast<uint32_t>(text.size())};
    PageBox box{100, 1000, 10, 20};
    const Page page = layoutPage(w, 0, box, mono);
    for (const Line& l : page.lines) TEST_ASSERT_TRUE(mono(text.data() + l.start, l.end - l.start) <= box.width);
    TEST_ASSERT_TRUE(page.lines[0].indent);
    TEST_ASSERT_EQUAL_STRING("Короткие", lineText(text, page.lines[0]).c_str());  // 8 chars + indent 20 = 100
    TEST_ASSERT_EQUAL_STRING("слова и", lineText(text, page.lines[1]).c_str());
    TEST_ASSERT_EQUAL_STRING("сверхдлинн", lineText(text, page.lines[2]).c_str());  // broken: 10 chars
    TEST_ASSERT_EQUAL(text.size(), page.next);
    const std::string hyphen = "черно-белый";
    const TextWindow h{hyphen, 0, static_cast<uint32_t>(hyphen.size())};
    TEST_ASSERT_EQUAL_STRING("черно-", lineText(hyphen, layoutPage(h, 0, PageBox{80, 100, 10, 0}, mono).lines[0]).c_str());
    const std::string empty;
    TEST_ASSERT_EQUAL(0, static_cast<int>(layoutPage(TextWindow{empty, 0, 0}, 0, box, mono).lines.size()));
}

static void test_paging_forward_then_back_is_the_same_pages() {
    const std::string text = book();
    const TextWindow w{text, 0, static_cast<uint32_t>(text.size())};
    const PageBox box{230, 120, 12, 20};
    std::vector<uint32_t> starts;
    for (uint32_t at = 0; at < text.size();) {
        starts.push_back(at);
        const Page p = layoutPage(w, at, box, mono);
        TEST_ASSERT_TRUE(p.next > at);
        TEST_ASSERT_TRUE(static_cast<int>(p.lines.size()) <= linesPerPage(box));
        at = p.next;
    }
    TEST_ASSERT_TRUE(starts.size() > 10);
    // back from every page: the page before ends exactly here, is full, and forward from it comes back
    for (size_t k = 1; k < starts.size(); k++) {
        const uint32_t current = starts[k];
        const uint32_t anchor = anchorBefore(text, 0, current, 600, 6000);
        TEST_ASSERT_TRUE(anchor == 0 || text[anchor - 1] == '\n');
        const Page back = pageBefore(w, anchor, current, box, mono);
        TEST_ASSERT_EQUAL(current, back.next);
        TEST_ASSERT_TRUE(back.start < current);
        const Page again = layoutPage(w, back.start, box, mono, current);
        TEST_ASSERT_EQUAL(current, again.next);
        TEST_ASSERT_EQUAL(back.lines.size(), again.lines.size());
        if (back.start > 0) TEST_ASSERT_TRUE(static_cast<int>(back.lines.size()) >= linesPerPage(box) - 1);
        for (size_t l = 0; l < back.lines.size(); l++) TEST_ASSERT_EQUAL(back.lines[l].start, again.lines[l].start);
    }
}

static void test_a_new_size_keeps_the_first_character_on_top() {
    const std::string text = book();
    const TextWindow w{text, 0, static_cast<uint32_t>(text.size())};
    uint32_t at = 0;
    for (int i = 0; i < 5; i++) at = layoutPage(w, at, PageBox{230, 120, 12, 20}, mono).next;
    // the same offset in a narrower, taller and in a wider box: the page starts with the same character
    for (const PageBox& box : {PageBox{150, 400, 16, 10}, PageBox{400, 200, 9, 30}}) {
        const Page p = layoutPage(w, at, box, mono);
        TEST_ASSERT_EQUAL(at, p.start);
        uint32_t first = at;
        while (text[first] == ' ' || text[first] == '\n') first++;
        TEST_ASSERT_EQUAL(first, p.lines[0].start);
    }
}

static void test_anchor_without_line_breaks_is_capped() {
    const std::string text(20000, 'a');
    TEST_ASSERT_EQUAL(15000u - 3000u, anchorBefore(text, 0, 15000, 1000, 3000));
    TEST_ASSERT_EQUAL(0u, anchorBefore(text, 0, 500, 1000, 3000));
    const std::string cyr = "яяяяяяяяяя";  // 2 bytes each: the anchor lands on a character
    TEST_ASSERT_EQUAL(0u, anchorBefore(cyr, 0, 20, 2, 5) % 2);
}

static void test_printable_and_fitting_the_title() {
    TEST_ASSERT_EQUAL_STRING("\"Ёж\" -- это...", printable("«Ёж» — это…", std::strlen("«Ёж» — это…")).c_str());
    TEST_ASSERT_EQUAL_STRING("Мастер и...", fitWidth("Мастер и Маргарита", 110, mono).c_str());
    TEST_ASSERT_EQUAL_STRING("Коротко", fitWidth("Коротко", 110, mono).c_str());
    TEST_ASSERT_EQUAL(0, percentOf(0, 1000));
    TEST_ASSERT_EQUAL(50, percentOf(500, 1000));
    TEST_ASSERT_EQUAL(100, percentOf(0, 0));
}

// ------------------------------------------------------------------ notes and folders

static void test_notes_round_trip_and_a_changed_book_starts_over() {
    CacheInfo c;
    c.sourceSize = 123456;
    c.sourceTime = 1700000000;
    c.textSize = 98765;
    c.title = "Мастер\nи Маргарита";
    CacheInfo back;
    TEST_ASSERT_TRUE(parseCacheInfo(formatCacheInfo(c), back));
    TEST_ASSERT_EQUAL(98765u, back.textSize);
    TEST_ASSERT_EQUAL_STRING("Мастер и Маргарита", back.title.c_str());
    TEST_ASSERT_TRUE(cacheFits(back, 123456, 1700000000));
    TEST_ASSERT_FALSE(cacheFits(back, 123457, 1700000000));
    TEST_ASSERT_FALSE(parseCacheInfo("garbage", back));

    Position p{4321, 98765, 123456, 1700000000};
    Position read;
    TEST_ASSERT_TRUE(parsePosition(formatPosition(p), read));
    TEST_ASSERT_EQUAL(4321u, placeIn(read, c));
    c.sourceTime++;
    TEST_ASSERT_EQUAL(0u, placeIn(read, c));
    TEST_ASSERT_FALSE(parsePosition("", read));
}

static void test_folders_first_then_books_alphabetically() {
    const auto list = listing({{"zeta.TXT", false}, {"Альфа.epub", false}, {"фото.jpg", false}, {".reader", true}, {"Ёлка.md", false},
                               {"books", true}, {"System Volume Information", true}, {"Архив", true}, {"бета.txt", false}});
    TEST_ASSERT_EQUAL(6, static_cast<int>(list.size()));
    TEST_ASSERT_EQUAL_STRING("books", list[0].name.c_str());  // folders first, Cyrillic after Latin
    TEST_ASSERT_EQUAL_STRING("Архив", list[1].name.c_str());
    TEST_ASSERT_TRUE(sortKey("books") < sortKey("Архив"));
    TEST_ASSERT_EQUAL_STRING("zeta.TXT", list[2].name.c_str());
    TEST_ASSERT_EQUAL_STRING("Альфа.epub", list[3].name.c_str());
    TEST_ASSERT_EQUAL_STRING("бета.txt", list[4].name.c_str());
    TEST_ASSERT_EQUAL_STRING("Ёлка.md", list[5].name.c_str());  // ё sorts as е
    TEST_ASSERT_TRUE(formatOf("a.Markdown") == Format::Md);
    TEST_ASSERT_TRUE(formatOf(".txt") == Format::None);
    TEST_ASSERT_EQUAL_STRING("/", parentOf("/a.txt").c_str());
    TEST_ASSERT_EQUAL_STRING("/книги", parentOf("/книги/a.txt").c_str());
    TEST_ASSERT_EQUAL_STRING("/a/b", joinPath("/a", "b").c_str());
    TEST_ASSERT_EQUAL_STRING("/b", joinPath("/", "b").c_str());
    TEST_ASSERT_EQUAL_STRING("Война и мир.fb", stem("/книги/Война и мир.fb.txt").c_str());
    TEST_ASSERT_EQUAL(10, firstRow(13, 10));
    TEST_ASSERT_EQUAL(4, stepRow(0, -1, 5));
    TEST_ASSERT_EQUAL(0, stepRow(4, 1, 5));
}

static void test_history_keeps_the_last_starts() {
    History<3> h;
    for (uint32_t s : {10u, 20u, 30u, 40u}) h.push(s);
    uint32_t s = 0;
    TEST_ASSERT_TRUE(h.pop(s));
    TEST_ASSERT_EQUAL(40u, s);
    TEST_ASSERT_TRUE(h.pop(s));
    TEST_ASSERT_TRUE(h.pop(s));
    TEST_ASSERT_EQUAL(20u, s);
    TEST_ASSERT_FALSE(h.pop(s));
}

// A 1-bit screen in memory, as GFXcanvas1 looks from outside.
struct FakeCanvas {
    int w, h;
    std::vector<int> px;
    FakeCanvas(int w_, int h_) : w(w_), h(h_), px(w_ * h_, 0) {}
    bool getPixel(int x, int y) const { return px[y * w + x] != 0; }
    void drawPixel(int x, int y, int c) { px[y * w + x] = c; }
};

static void test_a_chosen_row_is_the_same_text_flipped() {
    FakeCanvas c(8, 6);
    // some "text" in rows 1 and 2: the same pixels in the chosen row as in an ordinary one
    for (int x : {1, 2, 5}) { c.drawPixel(x, 1, 1); c.drawPixel(x, 4, 1); }
    FakeCanvas before = c;
    invertRect(c, 0, 3, 8, 2, c.w, c.h);  // rows 3 and 4
    for (int y = 0; y < c.h; y++)
        for (int x = 0; x < c.w; x++)
            TEST_ASSERT_EQUAL(y == 3 || y == 4 ? !before.getPixel(x, y) : before.getPixel(x, y), c.getPixel(x, y));
    // the text of row 4 (ink) is now white on a black bar
    TEST_ASSERT_FALSE(c.getPixel(1, 4));
    TEST_ASSERT_TRUE(c.getPixel(0, 4));
    // flipping twice is the original
    invertRect(c, 0, 3, 8, 2, c.w, c.h);
    TEST_ASSERT_TRUE(c.px == before.px);
}

static void test_inverting_stays_inside_the_screen() {
    FakeCanvas c(4, 4);
    invertRect(c, -2, -2, 10, 10, c.w, c.h);  // more than the screen: no pixel outside is touched, no crash
    for (int p : c.px) TEST_ASSERT_EQUAL(1, p);
    invertRect(c, 3, 3, 5, 5, c.w, c.h);
    TEST_ASSERT_EQUAL(0, c.getPixel(3, 3) ? 1 : 0);
    TEST_ASSERT_TRUE(c.getPixel(2, 3));
}

// ---- line spacing as a factor of the font height

static void test_line_height_is_the_font_height_times_the_factor_never_less_than_one_pixel_more() {
    TEST_ASSERT_EQUAL(14, lineHeightFor(13, 10));  // 1.0 would be 13: the minimum is the font height plus 1
    TEST_ASSERT_EQUAL(16, lineHeightFor(13, 12));  // 15.6 -> 16
    TEST_ASSERT_EQUAL(17, lineHeightFor(13, 13));  // 16.9 -> 17
    TEST_ASSERT_EQUAL(26, lineHeightFor(13, 20));
    TEST_ASSERT_EQUAL(24, lineHeightFor(20, 12));
    TEST_ASSERT_EQUAL(21, lineHeightFor(20, 10));  // the minimum again: 20 + 1
    for (int h = 8; h <= 40; h++)
        for (int t = kMinSpacingTenths; t <= kMaxSpacingTenths; t++) TEST_ASSERT_TRUE(lineHeightFor(h, t) >= h + 1);
}

static void test_the_factor_steps_from_1_0_to_2_0_and_round() {
    int t = kMinSpacingTenths;
    int steps = 0;
    do {
        t = nextSpacing(t);
        steps++;
    } while (t != kMinSpacingTenths && steps < 50);
    TEST_ASSERT_EQUAL(11, steps);  // eleven values: 1.0 1.1 ... 2.0
    TEST_ASSERT_EQUAL(kMinSpacingTenths, nextSpacing(kMaxSpacingTenths));
    TEST_ASSERT_EQUAL(13, nextSpacing(12));
    TEST_ASSERT_EQUAL(kDefaultSpacingTenths, clampSpacing(7));    // a stored value that makes no sense: the default
    TEST_ASSERT_EQUAL(kDefaultSpacingTenths, clampSpacing(99));
    TEST_ASSERT_EQUAL(12, kDefaultSpacingTenths);
}

static void test_more_spacing_fits_fewer_lines_and_the_page_starts_at_the_same_character() {
    std::string text;
    for (int i = 0; i < 400; i++) text += "слово ";
    TextWindow w{text, 0, static_cast<uint32_t>(text.size())};
    const uint32_t first = 6 * 37;  // somewhere in the middle: the first character on the page now
    PageBox tight{100, 300, lineHeightFor(12, 10), 0};
    PageBox loose{100, 300, lineHeightFor(12, 20), 0};
    const Page a = layoutPage(w, first, tight, mono);
    const Page b = layoutPage(w, first, loose, mono);
    TEST_ASSERT_TRUE(a.lines.size() > b.lines.size());          // roomier lines: fewer of them on a page
    TEST_ASSERT_EQUAL_UINT32(first, a.start);                    // the same first character at the top either way
    TEST_ASSERT_EQUAL_UINT32(a.start, b.start);
    TEST_ASSERT_EQUAL_UINT32(a.lines[0].start, b.lines[0].start);
    TEST_ASSERT_TRUE(b.next < a.next);                           // so the next page starts earlier with the looser spacing
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_utf8_passes_and_broken_bytes_become_one_mark_each);
    RUN_TEST(test_encodings_are_told_apart);
    RUN_TEST(test_txt_lines_are_paragraphs_with_one_gap_at_most);
    RUN_TEST(test_hand_wrapped_txt_is_joined_back);
    RUN_TEST(test_markdown_loses_its_marks);
    RUN_TEST(test_markup_becomes_paragraphs_and_entities_characters);
    RUN_TEST(test_epub_package_gives_title_and_reading_order);
    RUN_TEST(test_not_a_zip_and_no_title);
    RUN_TEST(test_lines_fit_and_long_words_break);
    RUN_TEST(test_paging_forward_then_back_is_the_same_pages);
    RUN_TEST(test_a_new_size_keeps_the_first_character_on_top);
    RUN_TEST(test_anchor_without_line_breaks_is_capped);
    RUN_TEST(test_printable_and_fitting_the_title);
    RUN_TEST(test_notes_round_trip_and_a_changed_book_starts_over);
    RUN_TEST(test_folders_first_then_books_alphabetically);
    RUN_TEST(test_history_keeps_the_last_starts);
    RUN_TEST(test_line_height_is_the_font_height_times_the_factor_never_less_than_one_pixel_more);
    RUN_TEST(test_the_factor_steps_from_1_0_to_2_0_and_round);
    RUN_TEST(test_more_spacing_fits_fewer_lines_and_the_page_starts_at_the_same_character);
    RUN_TEST(test_a_chosen_row_is_the_same_text_flipped);
    RUN_TEST(test_inverting_stays_inside_the_screen);
    return UNITY_END();
}
