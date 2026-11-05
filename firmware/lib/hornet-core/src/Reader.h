#pragma once
// The book reader of the CrowPanel 3.7" (docs/reader.md), kept free of Arduino so the host tests cover it: decoding txt (UTF-8,
// Windows-1251, UTF-16), stripping md and (X)HTML markup into one plain UTF-8 text, the zip directory and the epub package,
// laying out a page from a byte offset and finding the page before it, the notes kept on the card and the file list. Header
// only: the panel's firmware uses it without the rest of hornet-core.
//
// The text every book becomes: UTF-8, paragraphs end in '\n', an empty line ("\n\n") at most once in a row, no '\r', no tabs,
// no runs of spaces. A place in the book is the byte offset of the first character of a page in that text: it does not depend
// on the font, the size or the orientation.
#include <algorithm>
#include <cctype>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <string>
#include <vector>

namespace hornet::reader {

// ------------------------------------------------------------------ UTF-8

constexpr uint32_t kReplacement = 0xFFFD;

inline void appendUtf8(std::string& out, uint32_t cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

/// How many bytes a sequence with this lead byte should have (0: not a lead byte).
inline int utf8Length(uint8_t lead) {
    if (lead < 0x80) return 1;
    if (lead >= 0xC2 && lead <= 0xDF) return 2;
    if (lead >= 0xE0 && lead <= 0xEF) return 3;
    if (lead >= 0xF0 && lead <= 0xF4) return 4;
    return 0;
}

/// Decodes the character at s[i] and moves i past it. A broken sequence gives U+FFFD and moves one byte. A sequence cut by the end
/// of the buffer, whole so far, sets *complete to false and moves nothing (when <complete> is given; else it is a broken one).
inline uint32_t decodeUtf8(const char* s, size_t n, size_t& i, bool* complete = nullptr) {
    if (complete) *complete = true;
    const auto lead = static_cast<uint8_t>(s[i]);
    const int len = utf8Length(lead);
    if (len == 1) {
        i++;
        return lead;
    }
    if (len == 0) {
        i++;
        return kReplacement;
    }
    if (i + len > n) {
        bool whole = true;
        for (size_t k = i + 1; k < n; k++) whole = whole && (static_cast<uint8_t>(s[k]) & 0xC0) == 0x80;
        if (whole && complete) {
            *complete = false;
            return 0;
        }
        i++;
        return kReplacement;
    }
    uint32_t cp = lead & (0x7F >> len);
    for (int k = 1; k < len; k++) {
        const auto c = static_cast<uint8_t>(s[i + k]);
        if ((c & 0xC0) != 0x80) {
            i++;
            return kReplacement;
        }
        cp = (cp << 6) | (c & 0x3F);
    }
    static const uint32_t kLeast[] = {0, 0, 0x80, 0x800, 0x10000};
    if (cp < kLeast[len] || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) {
        i++;
        return kReplacement;
    }
    i += len;
    return cp;
}

/// The offset moved back to the start of the character it falls in.
inline size_t utf8Floor(const char* s, size_t offset) {
    while (offset > 0 && (static_cast<uint8_t>(s[offset]) & 0xC0) == 0x80) offset--;
    return offset;
}

// ------------------------------------------------------------------ encodings of a txt

enum class Encoding { Utf8, Utf16Le, Utf16Be, Cp1251 };

struct Detected {
    Encoding encoding = Encoding::Utf8;
    size_t bom = 0;  // bytes of the byte order mark to skip
};

/// What a txt is written in, from its first bytes: a BOM says so; else UTF-8 when the high bytes make valid sequences, Windows-1251
/// when they do not (Russian books of the 2000s); UTF-16 without a BOM is told by the zero bytes in every other place.
inline Detected detectEncoding(const uint8_t* p, size_t n) {
    if (n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF) return {Encoding::Utf8, 3};
    if (n >= 2 && p[0] == 0xFF && p[1] == 0xFE) return {Encoding::Utf16Le, 2};
    if (n >= 2 && p[0] == 0xFE && p[1] == 0xFF) return {Encoding::Utf16Be, 2};
    size_t zeroEven = 0, zeroOdd = 0;
    for (size_t i = 0; i < n; i++)
        if (p[i] == 0) (i % 2 ? zeroOdd : zeroEven)++;
    if (n >= 4 && zeroOdd * 3 > n / 2 && zeroEven == 0) return {Encoding::Utf16Le, 0};
    if (n >= 4 && zeroEven * 3 > n / 2 && zeroOdd == 0) return {Encoding::Utf16Be, 0};
    size_t good = 0, bad = 0;
    const auto* s = reinterpret_cast<const char*>(p);
    for (size_t i = 0; i < n;) {
        if (p[i] < 0x80) {
            i++;
            continue;
        }
        bool complete = true;
        const uint32_t cp = decodeUtf8(s, n, i, &complete);
        if (!complete) break;  // cut at the end of the sample
        (cp == kReplacement ? bad : good)++;
    }
    return {bad > 0 && bad * 50 > good ? Encoding::Cp1251 : Encoding::Utf8, 0};
}

/// Windows-1251 to Unicode.
inline uint32_t cp1251(uint8_t c) {
    static const uint16_t kHigh[64] = {
        0x0402, 0x0403, 0x201A, 0x0453, 0x201E, 0x2026, 0x2020, 0x2021, 0x20AC, 0x2030, 0x0409, 0x2039, 0x040A, 0x040C, 0x040B, 0x040F,
        0x0452, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0xFFFD, 0x2122, 0x0459, 0x203A, 0x045A, 0x045C, 0x045B, 0x045F,
        0x00A0, 0x040E, 0x045E, 0x0408, 0x00A4, 0x0490, 0x00A6, 0x00A7, 0x0401, 0x00A9, 0x0404, 0x00AB, 0x00AC, 0x00AD, 0x00AE, 0x0407,
        0x00B0, 0x00B1, 0x0406, 0x0456, 0x0491, 0x00B5, 0x00B6, 0x00B7, 0x0451, 0x2116, 0x0454, 0x00BB, 0x0458, 0x0405, 0x0455, 0x0457,
    };
    if (c < 0x80) return c;
    if (c >= 0xC0) return 0x0410 + (c - 0xC0);
    return kHigh[c - 0x80];
}

/// Turns bytes in an encoding into characters, chunk by chunk (a character may be split between chunks).
class Decoder {
public:
    explicit Decoder(Encoding encoding = Encoding::Utf8) : encoding_(encoding) {}

    template <class Emit>
    void feed(const uint8_t* p, size_t n, Emit&& emit) {
        for (size_t i = 0; i < n; i++) byte(p[i], emit);
    }

    /// The end of the input: a character cut short is a bad one.
    template <class Emit>
    void finish(Emit&& emit) {
        if (pendingLen_ || high_) emit(kReplacement);
        pendingLen_ = 0;
        high_ = 0;
    }

private:
    template <class Emit>
    void byte(uint8_t c, Emit& emit) {
        switch (encoding_) {
            case Encoding::Cp1251:
                emit(cp1251(c));
                return;
            case Encoding::Utf16Le:
            case Encoding::Utf16Be: {
                pending_[pendingLen_++] = c;
                if (pendingLen_ < 2) return;
                pendingLen_ = 0;
                const uint32_t unit = encoding_ == Encoding::Utf16Le ? (pending_[0] | pending_[1] << 8) : (pending_[0] << 8 | pending_[1]);
                if (unit >= 0xD800 && unit <= 0xDBFF) {
                    if (high_) emit(kReplacement);
                    high_ = unit;
                } else if (unit >= 0xDC00 && unit <= 0xDFFF) {
                    emit(high_ ? 0x10000 + ((high_ - 0xD800) << 10) + (unit - 0xDC00) : kReplacement);
                    high_ = 0;
                } else {
                    if (high_) emit(kReplacement);
                    high_ = 0;
                    emit(unit);
                }
                return;
            }
            case Encoding::Utf8:
                if (pendingLen_ == 0) {
                    if (c < 0x80) {
                        emit(c);
                        return;
                    }
                    if (utf8Length(c) == 0) {
                        emit(kReplacement);
                        return;
                    }
                } else if ((c & 0xC0) != 0x80) {
                    // the sequence broke off: it is one bad character, and this byte starts anew
                    pendingLen_ = 0;
                    emit(kReplacement);
                    byte(c, emit);
                    return;
                }
                pending_[pendingLen_++] = c;
                if (static_cast<int>(pendingLen_) == utf8Length(pending_[0])) {
                    size_t at = 0;
                    emit(decodeUtf8(reinterpret_cast<const char*>(pending_), pendingLen_, at));
                    pendingLen_ = 0;
                }
                return;
        }
    }

    Encoding encoding_;
    uint8_t pending_[4] = {};
    size_t pendingLen_ = 0;
    uint32_t high_ = 0;
};

// ------------------------------------------------------------------ the book's text

/// Builds the book's text (see the top of this file) from characters and paragraph marks; hands it on in pieces.
class TextWriter {
public:
    using Sink = std::function<void(const char*, size_t)>;
    explicit TextWriter(Sink sink) : sink_(std::move(sink)) {}

    void put(uint32_t cp) {
        if (cp == '\n') {
            paragraph();
            return;
        }
        if (cp == '\r' || cp == 0xFEFF || cp == 0x00AD || cp == 0x200B || cp == 0x200C || cp == 0x200D) return;  // invisible
        if (cp == ' ' || cp == '\t' || cp == 0x00A0 || cp == 0x2009 || cp == 0x2002 || cp == 0x2003 || cp == 0x202F || cp == '\f' || cp == '\v') {
            space_ = inParagraph_;
            return;
        }
        if (cp < 0x20 || cp == 0x7F) return;  // control characters
        if (gap_ && tail_ == 1) buffer_ += '\n';  // the empty line asked for, now that text follows it
        gap_ = false;
        if (space_) buffer_ += ' ';
        space_ = false;
        inParagraph_ = true;
        tail_ = 0;
        appendUtf8(buffer_, cp);
        if (buffer_.size() >= 512) flush();
    }

    void put(const std::string& utf8) {
        for (size_t i = 0; i < utf8.size();) put(decodeUtf8(utf8.data(), utf8.size(), i));
    }

    /// The end of a paragraph (nothing when none is open).
    void paragraph() {
        space_ = false;
        if (!inParagraph_) return;
        buffer_ += '\n';
        inParagraph_ = false;
        tail_ = 1;
    }

    /// An empty line: the end of the paragraph and a gap after it (once, not at the start nor at the end of the text).
    void blank() {
        paragraph();
        gap_ = true;
    }

    void flush() {
        if (buffer_.empty()) return;
        written_ += buffer_.size();
        sink_(buffer_.data(), buffer_.size());
        buffer_.clear();
    }

    /// Bytes of text so far (handed on or waiting).
    size_t size() const { return written_ + buffer_.size(); }

private:
    Sink sink_;
    std::string buffer_;
    size_t written_ = 0;
    bool space_ = false;
    bool inParagraph_ = false;
    bool gap_ = false;  // an empty line is due before the next text
    int tail_ = 2;      // 0: in text, 1: after a paragraph, 2: at the start
};

// ------------------------------------------------------------------ txt

/// Whether a txt is wrapped by hand at a fixed width (Project Gutenberg style: a paragraph is several lines), from the lengths of
/// its first lines in characters; such lines are joined back into paragraphs. Otherwise every line is a paragraph.
inline bool looksWrapped(const std::vector<size_t>& lengths) {
    size_t lines = 0, typical = 0, longer = 0;
    for (size_t len : lengths) {
        if (len == 0) continue;
        lines++;
        if (len >= 40 && len <= 100) typical++;
        if (len > 120) longer++;
    }
    return lines >= 20 && typical * 10 >= lines * 6 && longer * 20 < lines;
}

/// Lengths in characters of the lines of a UTF-8 text (for looksWrapped).
inline std::vector<size_t> lineLengths(const std::string& text) {
    std::vector<size_t> lengths;
    size_t chars = 0;
    for (size_t i = 0; i < text.size();) {
        const uint32_t cp = decodeUtf8(text.data(), text.size(), i);
        if (cp == '\n') {
            lengths.push_back(chars);
            chars = 0;
        } else if (cp != '\r') {
            chars++;
        }
    }
    return lengths;
}

/// A txt line by line into the text.
class PlainText {
public:
    PlainText(TextWriter& out, bool wrapped) : out_(out), wrapped_(wrapped) {}

    /// One line, without its end of line (decoded, UTF-8).
    void line(const std::string& text) {
        size_t chars = 0, lead = 0;
        bool leading = true;
        for (size_t i = 0; i < text.size();) {
            const uint32_t cp = decodeUtf8(text.data(), text.size(), i);
            if (cp == '\r') continue;
            chars++;
            if (leading && (cp == ' ' || cp == '\t' || cp == 0xA0)) lead++;
            else leading = false;
        }
        const size_t length = chars - lead;
        if (!wrapped_) {
            if (length == 0) out_.blank();
            else {
                out_.put(text);
                out_.paragraph();
            }
            return;
        }
        if (length == 0) {
            out_.paragraph();
            last_ = 0;
            return;
        }
        // a new paragraph: an indented line, or the line before was short (the end of a paragraph)
        if (lead > 0 || (last_ > 0 && last_ * 10 < widest_ * 6)) out_.paragraph();
        out_.put(text);
        out_.put(' ');
        last_ = length;
        widest_ = std::max(widest_, length);
    }

    void finish() { out_.paragraph(); }

private:
    TextWriter& out_;
    bool wrapped_;
    size_t last_ = 0, widest_ = 0;
};

// ------------------------------------------------------------------ md

inline bool isWordChar(uint32_t cp) {
    return (cp >= '0' && cp <= '9') || (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z') || (cp >= 0xC0 && cp != 0xD7 && cp != 0xF7);
}

/// The inline markup of md out of a line: emphasis and code marks, links (their text stays), images (gone), escapes.
inline std::string mdInline(const std::string& s) {
    std::string out;
    for (size_t i = 0; i < s.size();) {
        const char c = s[i];
        if (c == '\\' && i + 1 < s.size() && std::strchr("\\`*_{}[]()#+-.!|~<>", s[i + 1])) {
            out += s[i + 1];
            i += 2;
            continue;
        }
        if ((c == '!' && i + 1 < s.size() && s[i + 1] == '[') || c == '[') {
            const bool image = c == '!';
            const size_t open = image ? i + 1 : i;
            const size_t close = s.find(']', open + 1);
            if (close != std::string::npos && close + 1 < s.size() && (s[close + 1] == '(' || s[close + 1] == '[')) {
                const size_t end = s.find(s[close + 1] == '(' ? ')' : ']', close + 2);
                if (end != std::string::npos) {
                    if (!image) out += mdInline(s.substr(open + 1, close - open - 1));
                    i = end + 1;
                    continue;
                }
            }
        }
        if (c == '<' && (s.compare(i, 8, "<http://") == 0 || s.compare(i, 9, "<https://") == 0)) {
            const size_t end = s.find('>', i);
            if (end != std::string::npos) {
                out += s.substr(i + 1, end - i - 1);
                i = end + 1;
                continue;
            }
        }
        if (c == '*' || c == '`' || (c == '~' && i + 1 < s.size() && s[i + 1] == '~')) {
            i += c == '~' ? 2 : 1;
            continue;
        }
        if (c == '_') {
            // an underscore inside a word (snake_case) stays; at a word's edge it is emphasis
            uint32_t before = ' ';
            if (i > 0) {
                size_t at = utf8Floor(s.data(), i - 1);
                before = decodeUtf8(s.data(), s.size(), at);
            }
            size_t ahead = i + 1;
            while (ahead < s.size() && s[ahead] == '_') ahead++;
            uint32_t after = ' ';
            if (ahead < s.size()) {
                size_t at = ahead;
                after = decodeUtf8(s.data(), s.size(), at);
            }
            if (isWordChar(before) && isWordChar(after)) out.append(s, i, ahead - i);
            i = ahead;
            continue;
        }
        out += c;
        i++;
    }
    return out;
}

/// md line by line into the text: headings and list items stand alone, the lines of a paragraph are joined, code is kept as is,
/// tables become plain lines (cells apart), rules become a gap.
class MarkdownText {
public:
    explicit MarkdownText(TextWriter& out) : out_(out) {}

    void line(const std::string& raw) {
        std::string s = raw;
        while (!s.empty() && (s.back() == '\r' || s.back() == '\n')) s.pop_back();
        const bool hardBreak = s.size() >= 2 && s.compare(s.size() - 2, 2, "  ") == 0;
        while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.pop_back();
        size_t lead = 0;
        while (lead < s.size() && (s[lead] == ' ' || s[lead] == '\t')) lead++;
        const std::string t = s.substr(lead);

        if (t.compare(0, 3, "```") == 0 || t.compare(0, 3, "~~~") == 0) {
            out_.paragraph();
            code_ = !code_;
            open_ = false;
            return;
        }
        if (code_) {
            if (t.empty()) out_.blank();
            else {
                out_.put(s);
                out_.paragraph();
            }
            return;
        }
        if (t.empty()) {
            out_.paragraph();
            open_ = false;
            return;
        }
        if (isRule(t, '=') || isRule(t, '-') || isRule(t, '*') || isRule(t, '_')) {
            // under a line of text: that line was a heading; else a rule
            if (open_ && (t[0] == '=' || t[0] == '-')) out_.paragraph();
            else out_.blank();
            open_ = false;
            return;
        }
        if (t[0] == '#') {
            size_t level = 0;
            while (level < t.size() && t[level] == '#') level++;
            if (level <= 6 && (level == t.size() || t[level] == ' ')) {
                std::string text = t.substr(level);
                while (!text.empty() && (text.back() == '#' || text.back() == ' ')) text.pop_back();
                out_.blank();
                out_.put(mdInline(text));
                out_.paragraph();
                open_ = false;
                return;
            }
        }
        std::string body = t;
        const bool quote = body[0] == '>';
        if (quote != quote_) {  // a quote is a block of its own
            out_.paragraph();
            open_ = false;
            quote_ = quote;
        }
        while (!body.empty() && body[0] == '>') {
            body.erase(0, 1);
            while (!body.empty() && body[0] == ' ') body.erase(0, 1);
        }
        if (body.empty()) {
            out_.paragraph();
            open_ = false;
            return;
        }
        if (std::count(body.begin(), body.end(), '|') >= 2 && (body[0] == '|' || body.find(" | ") != std::string::npos)) {
            if (!isTableRule(body)) {
                out_.paragraph();
                out_.put(mdInline(tableCells(body)));
                out_.paragraph();
            }
            open_ = false;
            return;
        }
        const size_t digits = std::strspn(body.c_str(), "0123456789");
        if (body.size() >= 2 && (body[0] == '-' || body[0] == '*' || body[0] == '+') && body[1] == ' ') {
            out_.paragraph();
            out_.put("\xE2\x80\xA2 ");  // a bullet
            out_.put(mdInline(body.substr(2)));
        } else if (digits > 0 && digits < 10 && digits + 1 < body.size() && (body[digits] == '.' || body[digits] == ')') && body[digits + 1] == ' ') {
            out_.paragraph();
            out_.put(mdInline(body));
        } else {
            if (open_) out_.put(' ');
            out_.put(mdInline(body));
        }
        open_ = true;
        if (hardBreak) {
            out_.paragraph();
            open_ = false;
        }
    }

    void finish() { out_.paragraph(); }

private:
    static bool isRule(const std::string& t, char c) {
        size_t marks = 0;
        for (char x : t) {
            if (x == c) marks++;
            else if (x != ' ') return false;
        }
        return marks >= 3 || (c == '=' && marks >= 1);
    }
    static bool isTableRule(const std::string& t) {
        for (char x : t)
            if (x != '|' && x != '-' && x != ':' && x != ' ') return false;
        return t.find('-') != std::string::npos;
    }
    static std::string tableCells(const std::string& t) {
        std::string out, cell;
        const size_t start = t[0] == '|' ? 1 : 0;
        size_t end = t.size();
        if (end > start && t[end - 1] == '|') end--;
        for (size_t i = start; i <= end; i++) {
            if (i == end || t[i] == '|') {
                size_t a = 0, b = cell.size();
                while (a < b && cell[a] == ' ') a++;
                while (b > a && cell[b - 1] == ' ') b--;
                if (!out.empty()) out += " / ";
                out.append(cell, a, b - a);
                cell.clear();
            } else {
                cell += t[i];
            }
        }
        return out;
    }

    TextWriter& out_;
    bool code_ = false;
    bool quote_ = false;
    bool open_ = false;  // a paragraph is being joined from lines
};

// ------------------------------------------------------------------ (X)HTML

/// A named or numeric character reference (without & and ;) to its character, 0 when unknown.
inline uint32_t entity(const std::string& name) {
    if (name.size() > 1 && name[0] == '#') {
        const bool hex = name[1] == 'x' || name[1] == 'X';
        const char* digits = name.c_str() + (hex ? 2 : 1);
        if (!*digits) return 0;
        uint32_t cp = 0;
        for (const char* d = digits; *d; d++) {
            int v;
            if (*d >= '0' && *d <= '9') v = *d - '0';
            else if (hex && *d >= 'a' && *d <= 'f') v = *d - 'a' + 10;
            else if (hex && *d >= 'A' && *d <= 'F') v = *d - 'A' + 10;
            else return 0;
            cp = cp * (hex ? 16 : 10) + v;
            if (cp > 0x10FFFF) return kReplacement;
        }
        return cp == 0 || (cp >= 0xD800 && cp <= 0xDFFF) ? kReplacement : cp;
    }
    static const struct {
        const char* name;
        uint32_t cp;
    } kNamed[] = {
        {"amp", '&'},      {"lt", '<'},        {"gt", '>'},        {"quot", '"'},     {"apos", '\''},    {"nbsp", 0xA0},
        {"mdash", 0x2014}, {"ndash", 0x2013},  {"hellip", 0x2026}, {"laquo", 0xAB},   {"raquo", 0xBB},   {"ldquo", 0x201C},
        {"rdquo", 0x201D}, {"bdquo", 0x201E},  {"lsquo", 0x2018},  {"rsquo", 0x2019}, {"sbquo", 0x201A}, {"copy", 0xA9},
        {"reg", 0xAE},     {"trade", 0x2122},  {"deg", 0xB0},      {"shy", 0xAD},     {"times", 0xD7},   {"minus", 0x2212},
        {"middot", 0xB7},  {"bull", 0x2022},   {"thinsp", 0x2009}, {"ensp", 0x2002},  {"emsp", 0x2003},  {"zwnj", 0x200C},
        {"zwj", 0x200D},   {"numero", 0x2116}, {"sect", 0xA7},     {"para", 0xB6},    {"frac12", 0xBD},  {"euro", 0x20AC},
    };
    for (const auto& e : kNamed)
        if (name == e.name) return e.cp;
    return 0;
}

/// Markup into text, character by character: block elements end paragraphs, <br> ends a line, the head, scripts and styles are
/// skipped, entities are decoded, tags and comments vanish (images with them).
class MarkupText {
public:
    explicit MarkupText(TextWriter& out) : out_(out) {}

    void put(uint32_t cp) {
        switch (state_) {
            case State::Text:
                if (cp == '<') {
                    state_ = State::Tag;
                    tag_.clear();
                    quote_ = 0;
                } else if (cp == '&') {
                    state_ = State::Entity;
                    entity_.clear();
                } else if (skip_ == 0) {
                    out_.put(cp == '\n' || cp == '\r' || cp == '\t' ? ' ' : cp);
                }
                return;
            case State::Entity:
                if (cp == ';') {
                    state_ = State::Text;
                    if (skip_ > 0) return;
                    if (const uint32_t value = entity(entity_)) {
                        out_.put(value);
                    } else {
                        out_.put('&');
                        out_.put(entity_ + ";");
                    }
                } else if (entity_.size() < 12 && cp < 0x80 && (std::isalnum(static_cast<int>(cp)) || cp == '#')) {
                    entity_ += static_cast<char>(cp);
                } else {
                    // not an entity: a lone ampersand
                    state_ = State::Text;
                    if (skip_ == 0) {
                        out_.put('&');
                        out_.put(entity_);
                    }
                    put(cp);
                }
                return;
            case State::Tag:
                if (tag_ == "!--") {
                    state_ = State::Comment;
                    dashes_ = 0;
                    put(cp);
                    return;
                }
                if (quote_) {
                    if (cp == quote_) quote_ = 0;
                    return;
                }
                if (cp == '"' || cp == '\'') {
                    quote_ = cp;
                    return;
                }
                if (cp == '>') {
                    state_ = State::Text;
                    endTag();
                    return;
                }
                if (tag_.size() < 32) tag_ += cp < 0x80 ? static_cast<char>(cp) : ' ';
                return;
            case State::Comment:
                if (cp == '>' && dashes_ >= 2) state_ = State::Text;
                dashes_ = cp == '-' ? dashes_ + 1 : 0;
                return;
            case State::Raw:
                // a script or a style: only its end tag counts ("a<b" inside is no tag)
                if (cp == '<') {
                    tag_ = "<";
                } else if (!tag_.empty()) {
                    tag_ += cp < 0x80 ? static_cast<char>(std::tolower(static_cast<int>(cp))) : ' ';
                    const std::string end = "</" + raw_;
                    if (tag_ == end) {
                        state_ = State::Tag;  // the rest of the end tag as any tag
                        tag_ = "/" + raw_;
                        quote_ = 0;
                    } else if (end.compare(0, tag_.size(), tag_) != 0) {
                        tag_.clear();
                    }
                }
                return;
        }
    }

    void put(const std::string& utf8) {
        for (size_t i = 0; i < utf8.size();) put(decodeUtf8(utf8.data(), utf8.size(), i));
    }

    void finish() { out_.paragraph(); }

private:
    enum class State { Text, Tag, Entity, Comment, Raw };

    void endTag() {
        if (tag_.empty() || tag_[0] == '!' || tag_[0] == '?') return;  // doctype, CDATA, processing instructions
        const bool closing = tag_[0] == '/';
        const bool selfClosing = tag_.back() == '/';
        const size_t a = closing ? 1 : 0;
        size_t b = a;
        while (b < tag_.size() && tag_[b] != ' ' && tag_[b] != '/' && tag_[b] != '\t' && tag_[b] != '\n' && tag_[b] != '\r') b++;
        std::string name = tag_.substr(a, b - a);
        if (const size_t colon = name.find(':'); colon != std::string::npos) name = name.substr(colon + 1);  // xhtml:p
        for (char& c : name) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

        if (name == "head" || name == "script" || name == "style" || name == "svg" || name == "math") {
            if (selfClosing) return;
            if (closing) {
                skip_ = skip_ > 0 ? skip_ - 1 : 0;
            } else {
                skip_++;
                if (name == "script" || name == "style") {
                    state_ = State::Raw;
                    raw_ = name;
                    tag_.clear();
                }
            }
            return;
        }
        if (skip_ > 0) return;
        if (name == "br") {
            out_.paragraph();
        } else if (name == "hr") {
            out_.blank();
        } else if (name.size() == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6') {
            if (closing) out_.paragraph();
            else out_.blank();
        } else if (name == "li") {
            out_.paragraph();
            if (!closing) out_.put("\xE2\x80\xA2 ");
        } else if (name == "td" || name == "th") {
            out_.put(' ');
        } else if (isBlock(name)) {
            out_.paragraph();
        }
    }

    static bool isBlock(const std::string& n) {
        static const char* const kBlocks[] = {"p",      "div",    "ul",     "ol",         "blockquote", "tr",      "table",  "section",
                                              "article", "header", "footer", "aside",      "nav",        "dd",      "dt",     "dl",
                                              "pre",    "figure", "figcaption", "body",   "address",    "center",  "title",  "caption"};
        for (const char* b : kBlocks)
            if (n == b) return true;
        return false;
    }

    TextWriter& out_;
    State state_ = State::Text;
    std::string tag_, entity_;
    std::string raw_;  // the element whose text is not markup (script, style)
    uint32_t quote_ = 0;
    int dashes_ = 0;
    int skip_ = 0;
};

// ------------------------------------------------------------------ zip and epub

/// Reads <size> bytes at <offset> of a file into <into>; false when it cannot.
using ReadAt = std::function<bool(uint32_t offset, uint8_t* into, size_t size)>;

struct ZipEntry {
    std::string name;
    uint16_t method = 0;  // 0 stored, 8 deflated
    uint32_t compressedSize = 0;
    uint32_t size = 0;
    uint32_t headerOffset = 0;  // of the local header
};

inline uint16_t le16(const uint8_t* p) { return static_cast<uint16_t>(p[0] | p[1] << 8); }
inline uint32_t le32(const uint8_t* p) { return p[0] | p[1] << 8 | p[2] << 16 | static_cast<uint32_t>(p[3]) << 24; }

/// The central directory of a zip of <fileSize> bytes (no zip64, no encryption); empty when it is not a zip.
inline std::vector<ZipEntry> zipDirectory(const ReadAt& read, uint32_t fileSize) {
    std::vector<ZipEntry> entries;
    if (fileSize < 22) return entries;
    // the end record is in the last 22 bytes plus a comment of up to 64 KiB: look for it from the end
    const uint32_t tail = std::min<uint32_t>(fileSize, 22 + 65535);
    std::vector<uint8_t> buf(std::min<uint32_t>(tail, 2048));
    uint32_t end = 0;
    bool found = false;
    for (uint32_t back = 0; !found && back < tail;) {
        const uint32_t want = std::min<uint32_t>(static_cast<uint32_t>(buf.size()), tail - back);
        const uint32_t at = fileSize - back - want;
        if (want < 22 || !read(at, buf.data(), want)) break;
        for (int i = static_cast<int>(want) - 22; i >= 0; i--) {
            if (le32(&buf[i]) == 0x06054b50) {
                end = at + i;
                found = true;
                break;
            }
        }
        back += want - 21;  // overlap so a record across two windows is seen
    }
    if (!found) return entries;
    uint8_t rec[22];
    if (!read(end, rec, 22)) return entries;
    const uint16_t count = le16(rec + 10);
    const uint32_t dirSize = le32(rec + 12), dirOffset = le32(rec + 16);
    if (dirOffset > fileSize || dirSize > fileSize - dirOffset || count == 0xFFFF) return entries;
    uint32_t at = dirOffset;
    for (uint16_t i = 0; i < count; i++) {
        uint8_t h[46];
        if (!read(at, h, 46) || le32(h) != 0x02014b50) break;
        ZipEntry e;
        e.method = le16(h + 10);
        e.compressedSize = le32(h + 20);
        e.size = le32(h + 24);
        const uint16_t nameLen = le16(h + 28), extraLen = le16(h + 30), commentLen = le16(h + 32);
        e.headerOffset = le32(h + 42);
        e.name.resize(nameLen);
        if (nameLen && !read(at + 46, reinterpret_cast<uint8_t*>(&e.name[0]), nameLen)) break;
        entries.push_back(std::move(e));
        at += 46 + nameLen + extraLen + commentLen;
    }
    return entries;
}

/// Where the data of an entry starts (after its local header), 0 when the header is not there.
inline uint32_t zipDataOffset(const ReadAt& read, const ZipEntry& e) {
    uint8_t h[30];
    if (!read(e.headerOffset, h, 30) || le32(h) != 0x04034b50) return 0;
    return e.headerOffset + 30 + le16(h + 26) + le16(h + 28);
}

inline const ZipEntry* findEntry(const std::vector<ZipEntry>& entries, const std::string& name) {
    for (const auto& e : entries)
        if (e.name == name) return &e;
    for (const auto& e : entries) {  // some books differ in case only
        if (e.name.size() != name.size()) continue;
        bool same = true;
        for (size_t i = 0; same && i < name.size(); i++)
            same = std::tolower(static_cast<unsigned char>(e.name[i])) == std::tolower(static_cast<unsigned char>(name[i]));
        if (same) return &e;
    }
    return nullptr;
}

/// Markup and entities out of a short piece (a title, an attribute's value), on one line.
inline std::string plainText(const std::string& markup) {
    std::string out;
    TextWriter w([&](const char* s, size_t n) { out.append(s, n); });
    MarkupText m(w);
    m.put(markup);
    w.flush();
    while (!out.empty() && (out.back() == '\n' || out.back() == ' ')) out.pop_back();
    for (char& c : out)
        if (c == '\n') c = ' ';
    return out;
}

/// The value of attribute <name> in a start tag's text, entities decoded; empty when absent.
inline std::string attribute(const std::string& tag, const std::string& name) {
    for (size_t at = 0; (at = tag.find(name, at)) != std::string::npos; at += name.size()) {
        const char before = at == 0 ? ' ' : tag[at - 1];
        if (before != ' ' && before != '\t' && before != '\n' && before != '\r' && before != ':') continue;
        size_t i = at + name.size();
        while (i < tag.size() && tag[i] == ' ') i++;
        if (i >= tag.size() || tag[i] != '=') continue;
        i++;
        while (i < tag.size() && tag[i] == ' ') i++;
        if (i >= tag.size() || (tag[i] != '"' && tag[i] != '\'')) continue;
        const size_t end = tag.find(tag[i], i + 1);
        if (end == std::string::npos) return "";
        return plainText(tag.substr(i + 1, end - i - 1));
    }
    return "";
}

/// The name of the element at xml[at] == '<', without a namespace prefix; <full> gets it with the prefix.
inline std::string elementName(const std::string& xml, size_t at, std::string* full = nullptr, size_t* nameEnd = nullptr) {
    size_t b = at + 1;
    while (b < xml.size() && xml[b] != ' ' && xml[b] != '>' && xml[b] != '/' && xml[b] != '\t' && xml[b] != '\n' && xml[b] != '\r') b++;
    std::string name = xml.substr(at + 1, b - at - 1);
    if (full) *full = name;
    if (nameEnd) *nameEnd = b;
    if (const size_t colon = name.find(':'); colon != std::string::npos) name = name.substr(colon + 1);
    return name;
}

/// Every start tag named <name> (any namespace prefix) in an XML text, as the text between < and >.
inline std::vector<std::string> startTags(const std::string& xml, const std::string& name) {
    std::vector<std::string> tags;
    for (size_t at = 0; (at = xml.find('<', at)) != std::string::npos; at++) {
        size_t nameEnd = 0;
        if (elementName(xml, at, nullptr, &nameEnd) != name) continue;
        const size_t end = xml.find('>', nameEnd);
        if (end == std::string::npos) break;
        tags.push_back(xml.substr(at + 1, end - at - 1));
        at = end;
    }
    return tags;
}

/// The text inside the first element named <name> (any prefix), markup stripped.
inline std::string elementText(const std::string& xml, const std::string& name) {
    for (size_t at = 0; (at = xml.find('<', at)) != std::string::npos; at++) {
        std::string full;
        size_t nameEnd = 0;
        if (elementName(xml, at, &full, &nameEnd) != name) continue;
        const size_t open = xml.find('>', nameEnd);
        if (open == std::string::npos || xml[open - 1] == '/') return "";
        const size_t close = xml.find("</" + full, open);
        if (close == std::string::npos) return "";
        return plainText(xml.substr(open + 1, close - open - 1));
    }
    return "";
}

/// %20 and the like in an href.
inline std::string percentDecoded(const std::string& s) {
    std::string out;
    for (size_t i = 0; i < s.size(); i++) {
        if (s[i] == '%' && i + 2 < s.size() && std::isxdigit(static_cast<unsigned char>(s[i + 1])) && std::isxdigit(static_cast<unsigned char>(s[i + 2]))) {
            out += static_cast<char>(std::stoi(s.substr(i + 1, 2), nullptr, 16));
            i += 2;
        } else {
            out += s[i];
        }
    }
    return out;
}

/// A path inside the zip relative to the folder of <base> ("OEBPS/content.opf" + "text/ch1.xhtml"), with ".." resolved.
inline std::string resolvePath(const std::string& base, const std::string& href) {
    const std::string rel = percentDecoded(href.substr(0, href.find('#')));
    const size_t slash = base.rfind('/');
    const std::string joined = (slash == std::string::npos || (!rel.empty() && rel[0] == '/')) ? rel : base.substr(0, slash + 1) + rel;
    std::vector<std::string> parts;
    for (size_t start = 0; start <= joined.size();) {
        size_t end = joined.find('/', start);
        if (end == std::string::npos) end = joined.size();
        const std::string part = joined.substr(start, end - start);
        if (part == "..") {
            if (!parts.empty()) parts.pop_back();
        } else if (!part.empty() && part != ".") {
            parts.push_back(part);
        }
        start = end + 1;
    }
    std::string out;
    for (const auto& p : parts) out += (out.empty() ? "" : "/") + p;
    return out;
}

/// Where the package file is, from META-INF/container.xml.
inline std::string packagePath(const std::string& containerXml) {
    for (const auto& tag : startTags(containerXml, "rootfile")) {
        const std::string path = attribute(tag, "full-path");
        if (!path.empty()) return path;
    }
    return "";
}

struct Package {
    std::string title;
    std::vector<std::string> spine;  // the chapters in reading order, as paths inside the zip
};

/// The title and the reading order from the package (OPF) found at <path> in the zip.
inline Package readPackage(const std::string& opf, const std::string& path) {
    Package p;
    p.title = elementText(opf, "title");
    struct Item {
        std::string id, href, type;
    };
    std::vector<Item> items;
    for (const auto& tag : startTags(opf, "item")) items.push_back({attribute(tag, "id"), attribute(tag, "href"), attribute(tag, "media-type")});
    for (const auto& tag : startTags(opf, "itemref")) {
        const std::string idref = attribute(tag, "idref");
        for (const auto& item : items) {
            if (item.id != idref || item.href.empty()) continue;
            if (!item.type.empty() && item.type.find("html") == std::string::npos && item.type.find("xml") == std::string::npos) break;
            p.spine.push_back(resolvePath(path, item.href));
            break;
        }
    }
    return p;
}

// ------------------------------------------------------------------ layout

/// The width in pixels of a run of the text (UTF-8), in the font of the page.
using MeasureText = std::function<int(const char* s, size_t n)>;

struct PageBox {
    int width = 0, height = 0;  // of the text area
    int lineHeight = 1;
    int indent = 0;  // of the first line of a paragraph
};

struct Line {
    uint32_t start = 0, end = 0;  // the text drawn, [start, end) in the book's text; empty: a gap between paragraphs
    bool indent = false;
};

struct Page {
    uint32_t start = 0, next = 0;  // where it begins, and where the next one does
    std::vector<Line> lines;
};

inline int linesPerPage(const PageBox& box) { return std::max(1, box.height / std::max(1, box.lineHeight)); }

/// A window onto the book's text: <text> holds its bytes from <offset> on; <size> is the whole text's.
struct TextWindow {
    const std::string& text;
    uint32_t offset;
    uint32_t size;

    uint32_t end() const { return offset + static_cast<uint32_t>(text.size()); }
    const char* base() const { return text.data() - offset; }  // base()[i] is the byte at offset i
    /// Whether <pos> starts a paragraph (unknown at the window's own start: then only the text's start does).
    bool paragraphAt(uint32_t pos) const { return pos == 0 || (pos > offset && base()[pos - 1] == '\n'); }
};

/// One line from <pos> (not at a '\n'), at most to <limit>, <width> pixels wide; returns where the next line starts.
inline uint32_t layoutLine(const TextWindow& w, uint32_t pos, uint32_t limit, int width, const MeasureText& measure, Line& line) {
    const char* base = w.base();
    uint32_t start = pos;
    while (start < limit && base[start] == ' ') start++;
    uint32_t fits = start;  // the end of the last token that fits
    for (uint32_t i = start; i < limit;) {
        // the next token: spaces, then a word up to a space or a newline (a hyphen inside a word ends a token too)
        uint32_t wordEnd = i;
        while (wordEnd < limit && base[wordEnd] == ' ') wordEnd++;
        if (wordEnd >= limit || base[wordEnd] == '\n') break;
        while (wordEnd < limit && base[wordEnd] != ' ' && base[wordEnd] != '\n') {
            const bool hyphen = base[wordEnd] == '-';
            size_t at = wordEnd;
            decodeUtf8(base, limit, at);
            wordEnd = static_cast<uint32_t>(at);
            if (hyphen && wordEnd < limit && base[wordEnd] != ' ' && base[wordEnd] != '\n') break;
        }
        if (measure(base + start, wordEnd - start) <= width) {
            fits = wordEnd;
            i = wordEnd;
            continue;
        }
        if (fits == start) {
            // the word alone is wider than the line: as many characters as fit, at least one
            uint32_t c = i;
            while (c < wordEnd && base[c] == ' ') c++;
            bool any = false;
            while (c < wordEnd) {
                size_t at = c;
                decodeUtf8(base, wordEnd, at);
                if (any && measure(base + start, static_cast<uint32_t>(at) - start) > width) break;
                any = true;
                c = static_cast<uint32_t>(at);
            }
            fits = c;
        }
        break;
    }
    line.start = start;
    line.end = fits;
    uint32_t next = fits;
    while (next < limit && base[next] == ' ') next++;
    if (next < limit && base[next] == '\n') next++;  // the paragraph ends with this line
    return next;
}

/// The lines from <pos> on up to <limit> (at most <rows> of them; all when rows < 0). An empty line is a gap.
inline std::vector<Line> layoutLines(const TextWindow& w, uint32_t pos, uint32_t limit, int rows, const PageBox& box, const MeasureText& measure,
                                     uint32_t* next) {
    std::vector<Line> lines;
    const char* base = w.base();
    while ((rows < 0 || static_cast<int>(lines.size()) < rows) && pos < limit) {
        if (base[pos] == '\n') {
            lines.push_back({pos, pos, false});
            pos++;
            continue;
        }
        Line line;
        line.indent = w.paragraphAt(pos);
        pos = layoutLine(w, pos, limit, box.width - (line.indent ? box.indent : 0), measure, line);
        lines.push_back(line);
    }
    if (next) *next = pos;
    return lines;
}

/// The page that begins at <start>: as many lines as fit, at most up to <limit> (the end of the text when 0). The window holds the
/// text from a byte before <start> (to know a paragraph's start) on, a page and more or up to the end of the text. A gap is not
/// drawn on top of a page.
inline Page layoutPage(const TextWindow& w, uint32_t start, const PageBox& box, const MeasureText& measure, uint32_t limit = 0) {
    Page page;
    page.start = start;
    const uint32_t end = std::min(limit ? limit : w.size, w.end());
    uint32_t pos = start;
    if (pos < end && w.base()[pos] == '\n') pos++;
    page.lines = layoutLines(w, pos, end, linesPerPage(box), box, measure, &page.next);
    return page;
}

/// Where to start laying out to find the page before <current>: the start of a paragraph at least <minBack> bytes before it (the
/// same whatever the font), or <current> - <maxBack> (on a character) in a book without line breaks. <before> holds the text from
/// <beforeOffset> up to <current> at least.
inline uint32_t anchorBefore(const std::string& before, uint32_t beforeOffset, uint32_t current, uint32_t minBack, uint32_t maxBack) {
    if (current <= minBack) return 0;
    const uint32_t lowest = std::max(current > maxBack ? current - maxBack : 0, beforeOffset);
    for (uint32_t i = current - minBack; i > lowest; i--)
        if (before[i - 1 - beforeOffset] == '\n') return i;
    if (lowest == 0) return 0;
    return beforeOffset + static_cast<uint32_t>(utf8Floor(before.data(), lowest - beforeOffset));
}

/// The page that ends at <current>: the lines from <anchor> laid out up to <current>, the page full of the last ones. The window
/// holds the text from a byte before <anchor> to <current>. Its next is <current>, so forward from it comes back to the same place.
inline Page pageBefore(const TextWindow& w, uint32_t anchor, uint32_t current, const PageBox& box, const MeasureText& measure) {
    const std::vector<Line> lines = layoutLines(w, anchor, current, -1, box, measure, nullptr);
    const size_t rows = static_cast<size_t>(linesPerPage(box));
    size_t first = lines.size() > rows ? lines.size() - rows : 0;
    while (first < lines.size() && lines[first].start == lines[first].end && w.base()[lines[first].start] == '\n') first++;  // no gap on top
    Page page;
    page.next = current;
    if (first >= lines.size()) {
        page.start = anchor;
        return page;
    }
    page.start = lines[first].start;
    page.lines.assign(lines.begin() + static_cast<std::ptrdiff_t>(first), lines.end());
    return page;
}

/// What the panel's fonts can draw (ASCII and Cyrillic): typographic characters become their plain look-alikes.
inline std::string printable(const char* s, size_t n) {
    std::string out;
    out.reserve(n);
    for (size_t i = 0; i < n;) {
        const uint32_t cp = decodeUtf8(s, n, i);
        if ((cp >= 0x20 && cp < 0x7F) || (cp >= 0x400 && cp <= 0x45F)) {
            appendUtf8(out, cp);
            continue;
        }
        switch (cp) {
            case 0xAB: case 0xBB: case 0x201C: case 0x201D: case 0x201E: case 0x201F: case 0x2033: out += '"'; break;
            case 0x2018: case 0x2019: case 0x201A: case 0x201B: case 0x2032: case 0x2039: case 0x203A: out += '\''; break;
            case 0x2010: case 0x2011: case 0x2012: case 0x2013: case 0x2212: out += '-'; break;
            case 0x2014: case 0x2015: out += "--"; break;
            case 0x2026: out += "..."; break;
            case 0x2022: case 0xB7: case 0x25CF: out += '*'; break;
            case 0x2116: out += 'N'; break;
            case 0xA0: out += ' '; break;
            case 0xA9: out += "(c)"; break;
            case 0xAE: out += "(R)"; break;
            case 0x2122: out += "TM"; break;
            case 0xB0: out += 'o'; break;
            case 0xD7: out += 'x'; break;
            case 0xBD: out += "1/2"; break;
            case 0x20AC: out += "EUR"; break;
            case 0xE0: case 0xE1: case 0xE2: case 0xE4: out += 'a'; break;
            case 0xE8: case 0xE9: case 0xEA: case 0xEB: out += 'e'; break;
            case 0xEC: case 0xED: case 0xEE: case 0xEF: out += 'i'; break;
            case 0xF2: case 0xF3: case 0xF4: case 0xF6: out += 'o'; break;
            case 0xF9: case 0xFA: case 0xFB: case 0xFC: out += 'u'; break;
            case 0xE7: out += 'c'; break;
            case 0xF1: out += 'n'; break;
            case 0xDF: out += "ss"; break;
            default: out += '?'; break;
        }
    }
    return out;
}

/// <text> cut to <width> pixels with an ellipsis ("...") when it does not fit.
inline std::string fitWidth(const std::string& text, int width, const MeasureText& measure) {
    if (measure(text.data(), text.size()) <= width) return text;
    const int dots = measure("...", 3);
    size_t end = text.size();
    while (end > 0) {
        end = utf8Floor(text.data(), end - 1);
        if (measure(text.data(), end) + dots <= width) break;
    }
    std::string out = text.substr(0, end);
    while (!out.empty() && out.back() == ' ') out.pop_back();
    return out + "...";
}

inline int percentOf(uint32_t offset, uint32_t size) {
    if (size == 0) return 100;
    return static_cast<int>(std::min<uint64_t>(100, static_cast<uint64_t>(offset) * 100 / size));
}

// ------------------------------------------------------------------ the notes on the card

constexpr int kCacheVersion = 1;

/// What the cache of a book was made from: rebuilt when the book or the converter changed.
struct CacheInfo {
    int version = kCacheVersion;
    uint32_t sourceSize = 0, sourceTime = 0;
    uint32_t textSize = 0;
    std::string title;
};

inline std::string formatCacheInfo(const CacheInfo& c) {
    char head[96];
    std::snprintf(head, sizeof head, "vsreader %d\nsource %lu %lu\ntext %lu\ntitle ", c.version, static_cast<unsigned long>(c.sourceSize),
                  static_cast<unsigned long>(c.sourceTime), static_cast<unsigned long>(c.textSize));
    std::string title = c.title;
    std::replace(title.begin(), title.end(), '\n', ' ');
    return head + title + "\n";
}

inline bool parseCacheInfo(const std::string& s, CacheInfo& c) {
    unsigned long size = 0, time = 0, text = 0;
    int version = 0;
    if (std::sscanf(s.c_str(), "vsreader %d\nsource %lu %lu\ntext %lu", &version, &size, &time, &text) != 4) return false;
    c.version = version;
    c.sourceSize = static_cast<uint32_t>(size);
    c.sourceTime = static_cast<uint32_t>(time);
    c.textSize = static_cast<uint32_t>(text);
    c.title.clear();
    const size_t t = s.find("\ntitle ");
    if (t != std::string::npos) {
        const size_t end = s.find('\n', t + 7);
        c.title = s.substr(t + 7, end == std::string::npos ? std::string::npos : end - t - 7);
    }
    return true;
}

/// Whether a cache made as <cached> says is the text of the book as it is now.
inline bool cacheFits(const CacheInfo& cached, uint32_t sourceSize, uint32_t sourceTime) {
    return cached.version == kCacheVersion && cached.sourceSize == sourceSize && cached.sourceTime == sourceTime;
}

/// The reading place of a book: the offset in its text, and which text it was (a changed book drops it).
struct Position {
    uint32_t offset = 0;
    uint32_t textSize = 0;
    uint32_t sourceSize = 0, sourceTime = 0;
};

inline std::string formatPosition(const Position& p) {
    char out[96];
    std::snprintf(out, sizeof out, "offset %lu\ntext %lu\nsource %lu %lu\n", static_cast<unsigned long>(p.offset), static_cast<unsigned long>(p.textSize),
                  static_cast<unsigned long>(p.sourceSize), static_cast<unsigned long>(p.sourceTime));
    return out;
}

inline bool parsePosition(const std::string& s, Position& p) {
    unsigned long offset = 0, text = 0, size = 0, time = 0;
    if (std::sscanf(s.c_str(), "offset %lu\ntext %lu\nsource %lu %lu", &offset, &text, &size, &time) != 4) return false;
    p = {static_cast<uint32_t>(offset), static_cast<uint32_t>(text), static_cast<uint32_t>(size), static_cast<uint32_t>(time)};
    return true;
}

/// The saved place, if it is of this very book (same source, same text) and inside it; else the start.
inline uint32_t placeIn(const Position& p, const CacheInfo& book) {
    if (p.sourceSize != book.sourceSize || p.sourceTime != book.sourceTime || p.textSize != book.textSize || p.offset >= book.textSize) return 0;
    return p.offset;
}

// ------------------------------------------------------------------ the card's folders

enum class Format { None, Txt, Md, Epub };

inline std::string lowerAscii(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

/// What a file is, by its extension.
inline Format formatOf(const std::string& name) {
    const size_t dot = name.rfind('.');
    if (dot == std::string::npos || dot == 0) return Format::None;
    const std::string ext = lowerAscii(name.substr(dot + 1));
    if (ext == "txt") return Format::Txt;
    if (ext == "md" || ext == "markdown") return Format::Md;
    if (ext == "epub") return Format::Epub;
    return Format::None;
}

inline std::string baseName(const std::string& path) {
    const size_t slash = path.rfind('/');
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

/// The name without its folder and extension, as a title.
inline std::string stem(const std::string& path) {
    const std::string base = baseName(path);
    const size_t dot = base.rfind('.');
    return dot == std::string::npos || dot == 0 ? base : base.substr(0, dot);
}

/// The folder of a path ("/a/b.txt" -> "/a", "/b.txt" -> "/").
inline std::string parentOf(const std::string& path) {
    const size_t slash = path.rfind('/');
    if (slash == std::string::npos || slash == 0) return "/";
    return path.substr(0, slash);
}

inline std::string joinPath(const std::string& dir, const std::string& name) { return dir == "/" ? "/" + name : dir + "/" + name; }

struct Entry {
    std::string name;
    bool folder = false;
};

/// A name for sorting: case folded (Latin and Cyrillic), ё as е.
inline std::u32string sortKey(const std::string& name) {
    std::u32string key;
    for (size_t i = 0; i < name.size();) {
        uint32_t cp = decodeUtf8(name.data(), name.size(), i);
        if (cp >= 'A' && cp <= 'Z') cp += 32;
        else if (cp >= 0x410 && cp <= 0x42F) cp += 32;
        else if (cp >= 0x400 && cp <= 0x40F) cp += 80;
        if (cp == 0x451) cp = 0x435;
        key += static_cast<char32_t>(cp);
    }
    return key;
}

/// What the browser lists of a folder: subfolders, then books (txt, md, epub), each alphabetically; hidden ones (".name") and the
/// system's folders left out.
inline std::vector<Entry> listing(std::vector<Entry> all) {
    std::vector<Entry> out;
    for (auto& e : all) {
        if (e.name.empty() || e.name[0] == '.') continue;
        if (!e.folder && formatOf(e.name) == Format::None) continue;
        if (e.folder && (e.name == "System Volume Information" || lowerAscii(e.name) == "lost.dir")) continue;
        out.push_back(std::move(e));
    }
    std::stable_sort(out.begin(), out.end(), [](const Entry& a, const Entry& b) {
        if (a.folder != b.folder) return a.folder;
        return sortKey(a.name) < sortKey(b.name);
    });
    return out;
}

/// The row of the list that tops the screen with the cursor on <cursor>: the list goes by whole screens.
inline int firstRow(int cursor, int rows) { return rows <= 0 || cursor < 0 ? 0 : (cursor / rows) * rows; }

/// The cursor moved by <step> rows over <count> of them, round the ends.
inline int stepRow(int cursor, int step, int count) {
    if (count <= 0) return 0;
    return ((cursor + step) % count + count) % count;
}

/// A short ring of the starts of the pages read before, kept across sleep: paging back over them is exact.
template <int N>
struct History {
    uint32_t starts[N] = {};
    int count = 0;

    void push(uint32_t start) {
        if (count == N) {
            std::memmove(starts, starts + 1, sizeof(uint32_t) * (N - 1));
            count--;
        }
        starts[count++] = start;
    }
    bool pop(uint32_t& start) {
        if (count == 0) return false;
        start = starts[--count];
        return true;
    }
    void clear() { count = 0; }
};

}  // namespace hornet::reader
