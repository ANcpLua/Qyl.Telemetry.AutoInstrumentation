using System.Data;

namespace Qyl.Telemetry.AutoInstrumentation.Internal;

/// <summary>
/// Derives <c>db.operation.name</c> and <c>db.query.summary</c> from a command without parsing SQL:
/// the leading keyword, and the first schema object that follows FROM, INTO, JOIN, TABLE, UPDATE, or
/// EXEC in the statement's opening clause. The scan is fail-closed — it ends at the first character
/// it does not understand (any quote, comment, parameter, temp-table, or dialect marker), and a
/// candidate it cannot classify as a plain identifier is dropped, never guessed — so a value can
/// never reach the summary. The result is at most two tokens.
/// </summary>
internal static class QylDbQuerySummary
{
    private const string Call = "CALL";
    private const int MaxKeywordLength = 32;
    private const int MaxTargetLength = 128;
    private const int MaxWordsBeforeTarget = 64;

    // Words that may stand between the target keyword and the target itself.
    private static readonly HashSet<string> Skippable = new(StringComparer.Ordinal)
    {
        "IF", "NOT", "EXISTS", "ONLY", "TEMP", "TEMPORARY", "UNIQUE", "CLUSTERED", "NONCLUSTERED",
        "OR", "REPLACE", "GLOBAL", "LOCAL", "LOW_PRIORITY", "HIGH_PRIORITY", "DELAYED", "IGNORE", "QUICK",
        "TOP", "PERCENT",
    };

    // Words that are never a target; meeting one where a target is expected ends the search.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "SET", "WHERE", "VALUES", "ON", "AS", "USING",
        "WHEN", "FROM", "INTO", "JOIN", "TABLE", "VIEW", "INDEX", "PROCEDURE", "FUNCTION", "TRIGGER",
        "SEQUENCE", "SCHEMA", "DATABASE", "STDIN", "STDOUT", "DEFAULT", "NULL", "WITH", "RETURNING",
    };

    public static (string? Operation, string? Summary) Describe(CommandType commandType, string? commandText)
    {
        if (commandType is not CommandType.StoredProcedure)
            return Describe(commandText);

        var procedure = commandText?.Trim();
        return (Call, procedure is not null && IsTarget(procedure) ? Call + " " + procedure : Call);
    }

    private static (string? Operation, string? Summary) Describe(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return (null, null);

        var scanner = new Scanner(commandText);
        var operation = scanner.NextWord()?.ToUpperInvariant();
        if (operation is null || operation.Length > MaxKeywordLength)
            return (null, null);

        // A common-table-expression prefix names no operation; the statement's keyword follows it.
        if (operation is "WITH")
        {
            while (scanner.NextWord() is { } word)
            {
                var upper = word.ToUpperInvariant();
                if (upper is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                {
                    operation = upper;
                    break;
                }
            }
        }

        var target = Target(ref scanner, operation);
        return (operation, target is null ? operation : operation + " " + target);
    }

    private static string? Target(ref Scanner scanner, string operation)
    {
        var expected = operation is "UPDATE" or Call or "EXEC" or "EXECUTE";
        for (var words = 0; words < MaxWordsBeforeTarget; words++)
        {
            if (scanner.NextWord() is not { } word)
                return null;

            var upper = word.ToUpperInvariant();
            if (!expected)
            {
                expected = upper is "FROM" or "INTO" or "JOIN" or "TABLE" or "VIEW" or "INDEX" or "PROCEDURE" or "FUNCTION";
                continue;
            }

            if (Skippable.Contains(upper))
                continue;

            return !Reserved.Contains(upper) && IsTarget(word) ? word : null;
        }

        return null;
    }

    // schema.object: dot-joined plain identifiers, letters/digits/underscore, starting with a letter or underscore.
    private static bool IsTarget(string word)
    {
        if (word.Length is 0 or > MaxTargetLength)
            return false;

        var segmentStart = true;
        foreach (var character in word)
        {
            if (character is '.')
            {
                if (segmentStart)
                    return false;
                segmentStart = true;
                continue;
            }

            if (segmentStart ? !(char.IsLetter(character) || character is '_') : !(char.IsLetterOrDigit(character) || character is '_'))
                return false;
            segmentStart = false;
        }

        return !segmentStart;
    }

    /// <summary>
    /// Yields the words of the opening clause. Whitespace, comments, numbers, harmless operators, and
    /// balanced parenthesised groups are skipped; the first character outside that grammar — a quote,
    /// a parameter or variable marker, a temp-table or dollar marker, a statement separator — ends the
    /// scan for good.
    /// </summary>
    private ref struct Scanner(string text)
    {
        private int _position;
        private bool _stopped;

        public string? NextWord()
        {
            while (!_stopped && _position < text.Length)
            {
                var current = text[_position];

                if (char.IsWhiteSpace(current) || current is ',' or '*' or '=' or '<' or '>' or '+')
                {
                    _position++;
                    continue;
                }

                if (current is '-' && Peek(1) is '-')
                {
                    while (_position < text.Length && text[_position] is not '\n')
                        _position++;
                    continue;
                }

                if (current is '/' && Peek(1) is '*')
                {
                    SkipBlockComment();
                    continue;
                }

                if (current is '-' or '/')
                {
                    _position++;
                    continue;
                }

                if (char.IsDigit(current))
                {
                    while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] is '.'))
                        _position++;
                    continue;
                }

                if (current is '(')
                {
                    SkipGroup();
                    continue;
                }

                if (char.IsLetter(current) || current is '_')
                {
                    var start = _position;
                    while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '.'))
                        _position++;
                    return text[start.._position];
                }

                _stopped = true;
            }

            return null;
        }

        private char Peek(int offset)
            => _position + offset < text.Length ? text[_position + offset] : '\0';

        // SQL Server and PostgreSQL nest block comments, so "/* a /* b */ c */" is one comment. A
        // comment that never closes runs to the end of the text.
        private void SkipBlockComment()
        {
            var depth = 0;
            while (_position < text.Length)
            {
                if (text[_position] is '/' && Peek(1) is '*')
                {
                    depth++;
                    _position += 2;
                    continue;
                }

                if (text[_position] is '*' && Peek(1) is '/')
                {
                    _position += 2;
                    if (--depth is 0)
                        return;
                    continue;
                }

                _position++;
            }
        }

        // A balanced group holds sub-queries, column lists, and argument lists; nothing inside it is a
        // target, and a quote or marker inside it ends the scan exactly as it would outside.
        private void SkipGroup()
        {
            var depth = 0;
            while (_position < text.Length)
            {
                var current = text[_position++];
                if (current is '(')
                {
                    depth++;
                    continue;
                }

                if (current is ')')
                {
                    if (--depth is 0)
                        return;
                    continue;
                }

                if (!(char.IsLetterOrDigit(current) || char.IsWhiteSpace(current) || current is '_' or '.' or ',' or '*' or '=' or '<' or '>' or '+' or '-' or '/'))
                {
                    _stopped = true;
                    return;
                }
            }

            _stopped = true;
        }
    }
}
