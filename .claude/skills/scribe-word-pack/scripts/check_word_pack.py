#!/usr/bin/env python3
"""Check a Scribe word pack CSV before you import it.

    python check_word_pack.py FILE.csv [FILE.csv ...]
    python check_word_pack.py --ansi-code-page 1252 FILE.csv
    python check_word_pack.py --self-test PATH/TO/tests/fixtures/libraries/csv

Each file is read with the rules of Scribe's word pack import, so a row reported as skipped is a row the import
preview would skip. The check then lists what Scribe's word editor would flag, and a few problems the import accepts
without a word, such as the same spoken words twice with different spellings.

Exit status: 0 when every file can be imported as it is (warnings and notes allowed), 1 when a file has an error to
fix, 2 when a file could not be read at all.

Needs Python 3.8 or later and nothing else.

For maintainers: this mirrors the C# reader in src/Scribe.Core/Libraries (LibraryCsvCodec.ReadImport,
LibraryCsvRecords, LibraryFormulaGuard, LibraryLimits, LibraryTermLint, LibraryTermKey) and
CleanupPrompt.IsVocabularyReplacement. When any of those change, change this file to match, then run --self-test
against tests/fixtures/libraries/csv, which compares every import case in read-cases.json.
"""

from __future__ import annotations

import argparse
import codecs
import json
import os
import sys
import unicodedata
from dataclasses import dataclass, field
from typing import Dict, Iterator, List, Optional, Tuple

MAX_FIELD_LENGTH = 2_000  # LibraryLimits.MaxFieldLength, counted in UTF-16 code units as .NET counts them
MAX_TERMS = 50_000  # LibraryLimits.MaxTermsPerLibrary
MAX_IMPORT_BYTES = 10 * 1024 * 1024  # LibraryLimits.MaxImportBytes
LARGE_VOCABULARY = 10_000  # LibraryLimits.LargeVocabularyNoticeTerms
VOCABULARY_TERM_CHARS = 100  # CleanupPrompt.MaxGlossaryTermChars
FORMULA_GUARD_VERSION = 1  # LibraryFormulaGuard.Version
HEADER_COLUMNS = ("pattern", "replacement", "whole_word", "enabled")
METADATA_KEYS = ("name", "category", "description", "based-on", "scribe-format", "formula-guard")

# LibraryTermLint.CommonWords: everyday words in languages the speech model recognizes. A rule on one of them changes it
# in ordinary sentences.
COMMON_WORDS = frozenset(
    word.upper()
    for word in (
        "il", "di", "la", "le", "les", "de", "du", "des", "un", "une", "et", "en", "au", "ce",
        "se", "si", "su", "da", "del", "che", "non", "per", "con", "una", "el", "los", "las",
        "es", "als", "das", "der", "die", "den", "und", "ist", "im", "am", "an", "zu", "so",
        "no", "na", "os", "as", "em", "ao", "ou", "je", "tu", "me", "te", "ne", "on", "ma",
        "a", "i", "an", "as", "at", "be", "by", "do", "go", "he", "if", "in", "is", "it", "me",
        "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
    )
)

# LibraryTermLint.AlwaysLowercaseNames: names that really are always lowercase, compared exactly.
ALWAYS_LOWERCASE = frozenset(
    ("npm", "pnpm", "kubectl", "webpack", "pandas", "conda", "dbt", "htmx", "statsmodels", "torchvision", "torchaudio")
)

LINE_BREAKS = frozenset("\r\n\u000b\u000c\u0085\u2028\u2029")  # CleanupPrompt's line breaks
FORMULA_TRIGGERS = frozenset("=+-@\uff1d\uff0b\uff0d\uff20\t\r\n")  # LibraryFormulaGuard.IsTrigger
UNSPOKEN_SYMBOLS = frozenset('#@/\\_*+=<>|~^{}[]()$%&`"')  # characters speech recognition rarely writes

# Small words an abbreviation often skips: "statement of work" is SOW, "chief financial officer" is CFO.
SMALL_WORDS = frozenset(("a", "an", "and", "at", "by", "for", "in", "of", "on", "the", "to", "with"))

CODE_PAGE_NAMES = {
    65001: "UTF-8", 1200: "UTF-16 LE", 1201: "UTF-16 BE", 12000: "UTF-32 LE", 12001: "UTF-32 BE", 1252: "Windows-1252",
}
CODE_PAGE_CODECS = {65001: "utf-8", 1200: "utf-16-le", 1201: "utf-16-be", 12000: "utf-32-le", 12001: "utf-32-be"}


def _pass_undefined_bytes(error: UnicodeError) -> Tuple[str, int]:
    # Windows decodes the five bytes Windows-1252 leaves undefined as the C1 controls of the same value, and so does
    # .NET's code page 1252. Python's cp1252 codec refuses them, so hand them through the same way.
    if isinstance(error, UnicodeDecodeError):
        return "".join(chr(byte) for byte in error.object[error.start:error.end]), error.end
    raise error


codecs.register_error("scribe-undefined-bytes", _pass_undefined_bytes)


# ---- .NET string helpers ------------------------------------------------------------------------------------------


def is_white_space(ch: str) -> bool:
    """char.IsWhiteSpace: the Unicode White_Space characters .NET recognizes."""
    return ch in "\t\n\v\f\r\x85" or unicodedata.category(ch) in ("Zs", "Zl", "Zp")


def trim(value: str) -> str:
    start, end = 0, len(value)
    while start < end and is_white_space(value[start]):
        start += 1
    while end > start and is_white_space(value[end - 1]):
        end -= 1
    return value[start:end]


def trim_start(value: str) -> str:
    start = 0
    while start < len(value) and is_white_space(value[start]):
        start += 1
    return value[start:]


def is_blank(value: Optional[str]) -> bool:
    return value is None or all(is_white_space(ch) for ch in value)


def utf16_length(value: str) -> int:
    return len(value.encode("utf-16-le", "surrogatepass")) // 2


def _upper_one(ch: str) -> str:
    upper = ch.upper()
    return upper if len(upper) == 1 else ch


def _lower_one(ch: str) -> str:
    lower = ch.lower()
    return lower if len(lower) == 1 else ch


def fold(value: str) -> str:
    """What StringComparison.OrdinalIgnoreCase compares: each character's simple uppercase mapping."""
    return "".join(_upper_one(ch) for ch in value)


def lower_invariant(value: str) -> str:
    return "".join(_lower_one(ch) for ch in value)


def is_in_commit_form(spoken: str) -> bool:
    """LibraryTermKey.IsInCommitForm: no white space at the edges, and only single spaces inside."""
    if not spoken:
        return True
    if is_white_space(spoken[0]) or is_white_space(spoken[-1]):
        return False
    for index in range(1, len(spoken)):
        ch = spoken[index]
        if is_white_space(ch) and (ch != " " or spoken[index - 1] == " "):
            return False
    return True


def is_vocabulary_replacement(written: str) -> bool:
    """CleanupPrompt.IsVocabularyReplacement: one line of at most 100 characters, judged before any trimming."""
    return (
        not is_blank(written)
        and utf16_length(written) <= VOCABULARY_TERM_CHARS
        and not any(ch in LINE_BREAKS for ch in written)
    )


def spacing_problems(spoken: str) -> List[str]:
    """What keeps a spoken form out of commit form, in words, for the finding that reports it."""
    problems = []
    if spoken and is_white_space(spoken[0]):
        problems.append("starts with a space")
    if spoken and is_white_space(spoken[-1]):
        problems.append("ends with a space")
    inner = trim(spoken)
    if "  " in inner:
        problems.append("has two spaces in a row")
    if "\t" in inner:
        problems.append("has a tab")
    if any(is_white_space(ch) and ch not in " \t" for ch in inner):
        problems.append("has a no-break space or another unusual space")
    return problems


# ---- Reading, as LibraryCsvCodec.ReadImport reads ------------------------------------------------------------------


@dataclass
class Field:
    text: str
    quoted: bool


@dataclass
class Record:
    line: int
    fields: List[Field]
    raw_comment: bool


@dataclass
class RowError:
    line: int
    kind: str
    field: Optional[str] = None


@dataclass
class Term:
    spoken: str
    written: str
    whole_word: bool
    enabled: bool
    line: int = 0


@dataclass
class Encoding:
    code_page: int
    byte_order_mark: bool
    ansi_fallback: bool
    invalid_bytes_replaced: bool

    def describe(self) -> str:
        name = CODE_PAGE_NAMES.get(self.code_page, "code page %d" % self.code_page)
        if self.byte_order_mark:
            name += " with a byte order mark"
        if self.ansi_fallback:
            name = "not UTF-8, read as " + name
        return name


@dataclass
class Document:
    name: Optional[str]
    category: Optional[str]
    description: Optional[str]
    based_on: Optional[str]
    terms: List[Term]
    errors: List[RowError]
    encoding: Encoding
    formula_guard_version: Optional[int] = None
    issues: List[str] = field(default_factory=list)
    metadata: Dict[str, str] = field(default_factory=dict)


def _end_field(fields: List[Field], buffer: List[str], quoted: bool, first_quote_at: int) -> bool:
    text = "".join(buffer)
    buffer.clear()
    fields.append(Field(text, quoted))
    if len(fields) != 1:
        return False
    prefix = text if first_quote_at < 0 else text[:first_quote_at]
    trimmed = trim_start(prefix)
    return len(trimmed) > 0 and trimmed[0] == "#"


def read_records(text: str, errors: List[RowError]) -> Iterator[Record]:
    """LibraryCsvRecords.Read: 0.4.3's CSV reader, plus whether each field was quoted and each line a raw comment."""
    fields: List[Field] = []
    buffer: List[str] = []
    in_quotes = False
    field_quoted = False
    first_quote_at = -1
    raw_comment = False
    line = 1
    record_start = 1
    index, length = 0, len(text)
    while index < length:
        ch = text[index]
        if in_quotes:
            if ch == '"':
                if index + 1 < length and text[index + 1] == '"':
                    buffer.append('"')
                    index += 2
                    continue
                in_quotes = False
            else:
                if ch == "\n":
                    line += 1
                buffer.append(ch)
            index += 1
            continue

        if ch == '"':
            in_quotes = True
            if not field_quoted:
                field_quoted = True
                if not fields:
                    first_quote_at = len(buffer)
        elif ch == ",":
            raw_comment |= _end_field(fields, buffer, field_quoted, first_quote_at)
            field_quoted = False
        elif ch == "\r":
            pass
        elif ch == "\n":
            raw_comment |= _end_field(fields, buffer, field_quoted, first_quote_at)
            yield Record(record_start, fields, raw_comment)
            fields = []
            field_quoted = False
            first_quote_at = -1
            raw_comment = False
            line += 1
            record_start = line
        else:
            buffer.append(ch)
        index += 1

    if in_quotes:
        errors.append(RowError(record_start, "UnclosedQuote"))
        return

    if buffer or fields:
        raw_comment |= _end_field(fields, buffer, field_quoted, first_quote_at)
        yield Record(record_start, fields, raw_comment)


def _detect_byte_order_mark(data: bytes) -> Tuple[int, int]:
    """StreamReader's detection: (code page, length of the mark)."""
    if data[:2] == b"\xfe\xff":
        return 1201, 2
    if data[:2] == b"\xff\xfe":
        if len(data) >= 4 and data[2:4] == b"\x00\x00":
            return 12000, 4
        return 1200, 2
    if data[:3] == b"\xef\xbb\xbf":
        return 65001, 3
    if data[:4] == b"\x00\x00\xfe\xff":
        return 12001, 4
    return 65001, 0


def decode_managed(data: bytes) -> Tuple[str, Encoding]:
    code_page, mark = _detect_byte_order_mark(data)
    body = data[mark:]
    codec = CODE_PAGE_CODECS[code_page]
    text = body.decode(codec, "replace")
    try:
        body.decode(codec, "strict")
        valid = True
    except UnicodeDecodeError:
        valid = False
    return text, Encoding(code_page, mark > 0, False, not valid)


def decode_import(data: bytes, ansi_code_page: int) -> Tuple[str, Encoding]:
    text, encoding = decode_managed(data)
    if encoding.byte_order_mark or not encoding.invalid_bytes_replaced:
        return text, encoding
    codec = "cp%d" % ansi_code_page
    lenient = "scribe-undefined-bytes" if ansi_code_page == 1252 else "replace"
    strict = "scribe-undefined-bytes" if ansi_code_page == 1252 else "strict"
    text = data.decode(codec, lenient)
    try:
        data.decode(codec, strict)
        valid = True
    except UnicodeDecodeError:
        valid = False
    return text, Encoding(ansi_code_page, False, True, not valid)


def _record_is_blank(record: Record) -> bool:
    return all(not item.quoted and is_blank(item.text) for item in record.fields)


def _is_header(record: Record) -> bool:
    fields = record.fields
    count = len(fields)
    while count > 0 and not fields[count - 1].quoted and len(fields[count - 1].text) == 0:
        count -= 1
    if count < 2 or count > len(HEADER_COLUMNS):
        return False
    for index in range(count):
        if fold(trim(fields[index].text)) != fold(HEADER_COLUMNS[index]):
            return False
    return count > 2 or (not fields[0].quoted and not fields[1].quoted)


def _take_metadata(header: Dict[str, str], comment_line: str) -> bool:
    body = trim_start(comment_line.lstrip("#"))
    for key in METADATA_KEYS:
        if key in header:
            continue
        if len(body) <= len(key) or fold(body[: len(key)]) != fold(key) or body[len(key)] != ":":
            continue
        header[key] = trim(body[len(key) + 1:])
        return True
    return False


def _take_metadata_record(header: Dict[str, str], record: Record) -> bool:
    fields = record.fields
    count = len(fields)
    while count > 1 and not fields[count - 1].quoted and len(fields[count - 1].text) == 0:
        count -= 1
    line = fields[0].text if count == 1 else ",".join(item.text for item in fields[:count])
    _take_metadata(header, trim_start(line))
    return count < len(fields)


def _parse_version(value: Optional[str]) -> Optional[int]:
    if not value or not all("0" <= ch <= "9" for ch in value):
        return None
    number = int(value)
    return number if number <= 2_147_483_647 else None


def _parse_flag(value: Optional[str]) -> Tuple[bool, bool]:
    """Returns (understood, value); an empty or missing column means true."""
    if is_blank(value):
        return True, True
    word = lower_invariant(trim(value))
    if word in ("true", "yes", "1"):
        return True, True
    if word in ("false", "no", "0"):
        return True, False
    return False, True


def _starts_like_formula(value: str, start: int) -> bool:
    index = start
    while index < len(value) and value[index] == "'":
        index += 1
    while index < len(value) and value[index] == " ":
        index += 1
    return index < len(value) and value[index] in FORMULA_TRIGGERS


def _decode_formula_guard(value: str) -> str:
    return value[1:] if len(value) > 1 and value[0] == "'" and _starts_like_formula(value, 1) else value


def _strict_value(item: Field) -> str:
    return item.text if item.quoted else trim(item.text)


def _null_if_blank(value: Optional[str]) -> Optional[str]:
    return None if is_blank(value) else trim(value)


def _read_row(record: Record, reverse_guard: bool, terms: List[Term], errors: List[RowError]) -> None:
    fields = record.fields
    if len(fields) < 2:
        errors.append(RowError(record.line, "MissingFields"))
        return

    spoken = _strict_value(fields[0])
    written = _strict_value(fields[1])
    if reverse_guard:
        spoken = _decode_formula_guard(spoken)
        written = _decode_formula_guard(written)

    if is_blank(spoken):
        errors.append(RowError(record.line, "EmptySpoken"))
        return

    if utf16_length(spoken) > MAX_FIELD_LENGTH or utf16_length(written) > MAX_FIELD_LENGTH:
        too_long = spoken if utf16_length(spoken) > MAX_FIELD_LENGTH else written
        errors.append(RowError(record.line, "FieldTooLong", too_long))
        return

    understood, whole_word = _parse_flag(fields[2].text if len(fields) > 2 else None)
    if not understood:
        errors.append(RowError(record.line, "InvalidWholeWord", _strict_value(fields[2])))
        return

    understood, enabled = _parse_flag(fields[3].text if len(fields) > 3 else None)
    if not understood:
        errors.append(RowError(record.line, "InvalidEnabled", _strict_value(fields[3])))
        return

    terms.append(Term(spoken, written, whole_word, enabled, record.line))


def read_import(data: bytes, ansi_code_page: int = 1252) -> Document:
    """LibraryCsvCodec.ReadImport."""
    if len(data) > MAX_IMPORT_BYTES:
        _, declared = decode_managed(data[:4])
        declared.invalid_bytes_replaced = False
        return Document(None, None, None, None, [], [], declared, issues=["SizeLimitExceeded"])

    text, encoding = decode_import(data, ansi_code_page)
    header: Dict[str, str] = {}
    terms: List[Term] = []
    errors: List[RowError] = []
    issues: List[str] = []
    before_data = True
    reverse_guard = False
    data_records = 0

    for record in read_records(text, errors):
        if _record_is_blank(record):
            continue

        if before_data:
            if trim_start(record.fields[0].text).startswith("#"):
                if _take_metadata_record(header, record) and "HeaderPaddingRemoved" not in issues:
                    issues.append("HeaderPaddingRemoved")
                continue
            before_data = False
            reverse_guard = _parse_version(header.get("formula-guard")) == FORMULA_GUARD_VERSION
            if _is_header(record):
                continue
        elif record.raw_comment:
            continue

        if data_records == MAX_TERMS:
            issues.append("RowLimitExceeded")
            break

        data_records += 1
        _read_row(record, reverse_guard, terms, errors)

    return Document(
        _null_if_blank(header.get("name")),
        _null_if_blank(header.get("category")),
        _null_if_blank(header.get("description")),
        _null_if_blank(header.get("based-on")),
        terms,
        errors,
        encoding,
        _parse_version(header.get("formula-guard")),
        issues,
        header,
    )


# ---- Checking ------------------------------------------------------------------------------------------------------

ROW_ERROR_TEXT = {
    "MissingFields": "has one value; a row needs what Scribe hears and what it writes, separated by a comma",
    "EmptySpoken": "has nothing in the first column (what Scribe hears)",
    "InvalidWholeWord": "has %s in the whole_word column; use true or false",
    "InvalidEnabled": "has %s in the enabled column; use true or false",
    "UnclosedQuote": "opens a double quote that is never closed, so no row from here on is read",
    "FieldTooLong": "has a value longer than %d characters" % MAX_FIELD_LENGTH,
}


@dataclass
class Finding:
    level: str  # ERROR, WARNING or NOTE
    line: int
    text: str


def _quote(value: str, limit: int = 60) -> str:
    shown = value if len(value) <= limit else value[: limit - 3] + "..."
    return json.dumps(shown, ensure_ascii=False)


def _is_initialism(words: List[str], abbreviation: str) -> bool:
    """Whether the abbreviation's letters are the words' initials, with or without the small words, or a plural."""
    letters = fold("".join(ch for ch in abbreviation if ch.isalnum()))
    if len(letters) < 2 or len(words) < 2:
        return False
    forms = {letters}
    if abbreviation.endswith("s") and len(letters) > 2:
        forms.add(letters[:-1])
    initials = {
        fold("".join(word[0] for word in words)),
        fold("".join(word[0] for word in words if lower_invariant(word) not in SMALL_WORDS)),
    }
    return bool(forms & initials)


def _changes_the_words(spoken: str, written: str) -> Optional[str]:
    """Whether a row swaps a phrase and its abbreviation instead of fixing a spelling: "shortens", "expands" or None."""
    spoken_words = spoken.split()
    written_words = written.split()
    if not spoken_words or not written_words:
        return None
    if len(spoken_words) == 1 and len(written_words) == 1:
        # A word cut down to a dotted abbreviation of itself: "versus" as "v.", "circuit" as "Cir.".
        spoken_letters = fold("".join(ch for ch in spoken if ch.isalnum()))
        written_letters = fold("".join(ch for ch in written if ch.isalnum()))
        shortened = 0 < len(written_letters) < len(spoken_letters) and spoken_letters.startswith(written_letters)
        return "shortens" if shortened and written.endswith(".") else None
    letters_spelled = all(len(word) == 1 for word in spoken_words[:-1]) and len(spoken_words[-1]) <= 2
    if len(spoken_words) >= 2 and not letters_spelled and len(written_words) == 1:
        return "shortens" if _is_initialism(spoken_words, written) else None
    if (len(spoken_words) == 1 or letters_spelled) and len(written_words) >= 2:
        return "expands" if _is_initialism(written_words, "".join(spoken_words)) else None
    return None


def check(document: Document) -> List[Finding]:
    findings: List[Finding] = []

    def add(level: str, line: int, text: str) -> None:
        findings.append(Finding(level, line, text))

    if "SizeLimitExceeded" in document.issues:
        add("ERROR", 0, "The file is larger than 10 MB, so Scribe doesn't read it. Split it into smaller word packs.")
        return findings

    if document.encoding.invalid_bytes_replaced:
        add("ERROR", 0, "Some bytes aren't valid text (%s). Save the file as UTF-8." % document.encoding.describe())
    elif document.encoding.ansi_fallback:
        add("WARNING", 0, "The file isn't UTF-8 (%s). Save it as UTF-8 so every letter comes through the same on "
            "every PC." % document.encoding.describe())
    elif document.encoding.code_page != 65001:
        add("NOTE", 0, "The file is %s. Scribe reads it, but UTF-8 is the safest choice." % document.encoding.describe())

    for key in ("name", "category", "description"):
        value = document.metadata.get(key)
        if value is not None and '"' in value:
            add("ERROR", 0, "The %s has a double quote (\"). Scribe refuses one in a word pack's name, category and "
                "description, so remove it." % key)

    if document.name is None:
        add("NOTE", 0, "No '# name:' line, so Scribe names the word pack after the file.")
    if "based-on" in document.metadata or "scribe-format" in document.metadata:
        add("NOTE", 0, "'# based-on:' and '# scribe-format:' are for files Scribe writes itself. Leave them out.")
    if document.formula_guard_version is not None:
        add("NOTE", 0, "'# formula-guard: %d' makes Scribe remove one apostrophe from a value that starts with one "
            "before =, +, - or @. Keep it in a file Scribe exported; leave it out of a file you write yourself."
            % document.formula_guard_version)
    if "HeaderPaddingRemoved" in document.issues:
        add("NOTE", 0, "Empty cells after the '#' lines were ignored (a spreadsheet adds them).")
    if "RowLimitExceeded" in document.issues:
        add("ERROR", 0, "The file has more than %d rows, and Scribe stops reading there. Split it into smaller word "
            "packs." % MAX_TERMS)

    for error in document.errors:
        reason = ROW_ERROR_TEXT.get(error.kind, error.kind)
        if "%s" in reason:
            reason = reason % _quote(error.field or "")
        add("ERROR", error.line, "Scribe skips this row: it %s." % reason)

    if not document.terms and not document.errors:
        add("ERROR", 0, "The file has no rows with words in them.")

    first_by_key: Dict[str, Term] = {}
    for term in document.terms:
        spoken, written = term.spoken, term.written
        trimmed_spoken, trimmed_written = trim(spoken), trim(written)
        key = fold(trimmed_spoken)

        if key == "PATTERN":
            add("WARNING", term.line, "This looks like a second header row. Scribe reads it as a word, so keep one "
                "header row, at the top.")

        earlier = first_by_key.get(key)
        if earlier is None:
            first_by_key[key] = term
        elif (earlier.written, earlier.whole_word, earlier.enabled) == (written, term.whole_word, term.enabled):
            add("WARNING", term.line, "%s repeats line %d exactly. Remove one of them."
                % (_quote(trimmed_spoken), earlier.line))
        else:
            add("ERROR", term.line, "%s is also on line %d with a different spelling or setting. The import keeps "
                "line %d unless you choose Use the file's version, so keep one row for it."
                % (_quote(trimmed_spoken), earlier.line, earlier.line))

        if not is_in_commit_form(spoken):
            problems = spacing_problems(spoken) or ["has irregular spacing"]
            described = problems[0] if len(problems) == 1 else ", ".join(problems[:-1]) + " and " + problems[-1]
            add("ERROR", term.line, "%s %s, so it can miss what you dictate or swallow the space before it. Use "
                "single spaces between words, and none at the ends." % (_quote(spoken), described))

        if key in COMMON_WORDS:
            add("WARNING", term.line, "%s is an everyday word, so this row also changes it in ordinary sentences. "
                "Use a longer phrase, or leave it to AI cleanup." % _quote(trimmed_spoken))

        if not term.whole_word:
            add("WARNING", term.line, "whole_word is false, so %s also changes inside longer words. Keep it true "
                "unless you mean that." % _quote(trimmed_spoken))

        if (trimmed_written
                and key == fold(trimmed_written)
                and trimmed_written == lower_invariant(trimmed_written)
                and trimmed_written not in ALWAYS_LOWERCASE
                and any(_upper_one(ch) != ch for ch in trimmed_written)):
            add("WARNING", term.line, "%s only makes the word lowercase, even at the start of a sentence. Remove the "
                "row unless the name is always written in lowercase." % _quote(trimmed_written))

        change = _changes_the_words(trimmed_spoken, trimmed_written) if trimmed_written else None
        if change == "shortens":
            add("WARNING", term.line, "This row shortens %s to %s wherever you say it, which changes your words "
                "rather than their spelling. Remove the row unless you want that."
                % (_quote(trimmed_spoken), _quote(trimmed_written)))
        elif change == "expands":
            add("WARNING", term.line, "This row spells %s out as %s wherever you say it, which changes your words "
                "rather than their spelling. Remove the row unless you want that."
                % (_quote(trimmed_spoken), _quote(trimmed_written)))

        if written == "":
            add("WARNING", term.line, "The second column is empty, so this row deletes %s from what you dictate."
                % _quote(trimmed_spoken))
        elif is_blank(written):
            add("WARNING", term.line, "The second column is only spaces, so this row replaces %s with spaces."
                % _quote(trimmed_spoken))
        else:
            if written != trimmed_written:
                add("WARNING", term.line, "%s has a space at its start or end, which Scribe types too."
                    % _quote(written))
            if not is_vocabulary_replacement(written):
                add("NOTE", term.line, "What Scribe writes here is longer than %d characters or spans lines. It still "
                    "applies on your PC, but AI cleanup doesn't get it as vocabulary. For a signature or other saved "
                    "text, a voice snippet fits better." % VOCABULARY_TERM_CHARS)

        symbols = sorted({ch for ch in trimmed_spoken if ch in UNSPOKEN_SYMBOLS})
        if symbols:
            add("NOTE", term.line, "%s has %s, which speech recognition rarely writes. Check what Scribe hears in "
                "Try dictation." % (_quote(trimmed_spoken), " ".join(symbols)))

        if not term.enabled:
            add("NOTE", term.line, "enabled is false, so this row is imported but not used until you turn it on.")

    if len(document.terms) > LARGE_VOCABULARY:
        add("WARNING", 0, "%d words is a lot for one word pack. Every entry can change what you dictate, and AI "
            "cleanup's vocabulary holds at most 5,000 words, so keep it to the words people use."
            % len(document.terms))

    return findings


def report(path: str, document: Document, findings: List[Finding], out) -> Tuple[int, int]:
    print("== %s" % path, file=out)
    details = []
    if document.name:
        details.append("word pack %s" % _quote(document.name))
    if document.category:
        details.append("category %s" % _quote(document.category))
    details.append(document.encoding.describe())
    print("   " + ", ".join(details), file=out)
    words, skipped = len(document.terms), len(document.errors)
    print("   %d word%s to import, %d row%s skipped" % (
        words, "" if words == 1 else "s", skipped, "" if skipped == 1 else "s"), file=out)

    order = {"ERROR": 0, "WARNING": 1, "NOTE": 2}
    for finding in sorted(findings, key=lambda item: (order[item.level], item.line)):
        where = "line %d" % finding.line if finding.line else "file"
        print("   %-7s %-9s %s" % (finding.level, where, finding.text), file=out)

    errors = sum(1 for item in findings if item.level == "ERROR")
    warnings = sum(1 for item in findings if item.level == "WARNING")
    if errors:
        verdict = "Fix the errors, then check again."
    elif warnings:
        verdict = "Ready to import. Review the warnings first."
    else:
        verdict = "Ready to import."
    print("   %d error%s, %d warning%s. %s" % (
        errors, "" if errors == 1 else "s", warnings, "" if warnings == 1 else "s", verdict), file=out)
    return errors, warnings


# ---- Self-test against the shared fixtures -------------------------------------------------------------------------


def as_fixture(document: Document) -> dict:
    """The document in the shape of read-cases.json's "expected" objects."""
    return {
        "name": document.name,
        "category": document.category,
        "description": document.description,
        "basedOn": document.based_on,
        "terms": [
            {"spoken": term.spoken, "written": term.written, "wholeWord": term.whole_word, "enabled": term.enabled}
            for term in document.terms
        ],
        "errors": [{"line": error.line, "kind": error.kind, "field": error.field} for error in document.errors],
        "encoding": {
            "codePage": document.encoding.code_page,
            "byteOrderMark": document.encoding.byte_order_mark,
            "ansiFallback": document.encoding.ansi_fallback,
            "invalidBytesReplaced": document.encoding.invalid_bytes_replaced,
        },
        "formulaGuardVersion": document.formula_guard_version,
        "issues": sorted(document.issues),
    }


def self_test(fixtures: str) -> int:
    with open(os.path.join(fixtures, "read-cases.json"), encoding="utf-8") as handle:
        cases = json.load(handle)
    ansi = int(cases.get("ansiCodePage", 1252))
    failures = 0
    compared = 0
    for case in cases["cases"]:
        if case["read"] != "import":
            continue
        compared += 1
        with open(os.path.join(fixtures, case["file"]), "rb") as handle:
            actual = as_fixture(read_import(handle.read(), ansi))
        expected = dict(case["expected"])
        expected["issues"] = sorted(expected.get("issues") or [])
        differences = [key for key in expected if expected[key] != actual.get(key)]
        if differences:
            failures += 1
            print("FAIL %s: %s" % (case["file"], ", ".join(differences)))
            for key in differences:
                print("  expected %s: %s" % (key, json.dumps(expected[key], ensure_ascii=False)))
                print("  actual   %s: %s" % (key, json.dumps(actual.get(key), ensure_ascii=False)))
        else:
            print("ok   %s" % case["file"])
    print("%d of %d import cases match." % (compared - failures, compared))
    return 1 if failures or compared == 0 else 0


def main(argv: Optional[List[str]] = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")
    parser = argparse.ArgumentParser(
        description="Check Scribe word pack CSV files the way Scribe's import reads them.")
    parser.add_argument("files", nargs="*", help="word pack CSV files to check")
    parser.add_argument("--ansi-code-page", type=int, default=1252,
                        help="code page Scribe falls back to for a file that isn't UTF-8 (default 1252, Western)")
    parser.add_argument("--self-test", metavar="FIXTURES",
                        help="compare against tests/fixtures/libraries/csv in the Scribe repository")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test(args.self_test)
    if not args.files:
        parser.print_usage()
        return 2

    status = 0
    for path in args.files:
        try:
            with open(path, "rb") as handle:
                data = handle.read()
        except OSError as error:
            print("== %s\n   Couldn't read the file: %s" % (path, error.strerror or error))
            status = 2
            continue
        document = read_import(data, args.ansi_code_page)
        errors, _ = report(path, document, check(document), sys.stdout)
        if errors and status == 0:
            status = 1
    return status


if __name__ == "__main__":
    sys.exit(main())
