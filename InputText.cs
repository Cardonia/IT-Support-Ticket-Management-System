// Text that must never reach the database as it is (Level 34).
//
// PostgreSQL text can not hold a NUL character (0x00): a statement with one fails ("invalid byte sequence"), which the
// application would answer with a 500. Other control characters (bell, escape, backspace, DEL ...) are never part of an
// honest title, note, name or password and only make logs and screens confusing. A lone half of a surrogate pair is not
// valid text either and fails when it is encoded as UTF-8.
// Every place that takes free text from a request checks it with these helpers and answers 400, so the answer is a
// sentence and not "Server error".
internal static class InputText
{
    // Control characters other than the three that belong in multi-line text: tab, line feed, carriage return
    public static bool HasForbiddenControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                return true;
        return HasLoneSurrogate(text);
    }

    // Stricter: any control character at all (single-line values such as a password or a search box)
    public static bool HasAnyControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                return true;
        return HasLoneSurrogate(text);
    }

    static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(text[i])) return true;
        }
        return false;
    }

    public const string TextMessage = "can not contain control characters.";
}
