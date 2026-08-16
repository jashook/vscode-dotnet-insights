////////////////////////////////////////////////////////////////////////////////
// Module: Utf16StringPool.cs
//
// Notes:
// Maps a UTF-16 char span (a slice of a payload buffer, already in the
// encoding the wire format uses) to ONE canonical string instance, allocating
// only the first time a given content is seen.
//
// The wire format stores every name as null-terminated UTF-16, and this
// parser's per-event decode used to call Encoding.Unicode.GetString for each
// occurrence - allocating a brand new string per event even though a capture
// only contains a few dozen distinct exception type names, allocation type
// names and so on. On a real 3.23GB capture with 1,443,601 exceptions,
// UnicodeEncoding.GetCharCount/GetChars plus the null-terminator scan were
// over half of the whole exception projection phase, producing 2.9M strings
// where a few hundred would do.
//
// Backed by HashSet<string>'s ReadOnlySpan<char> ALTERNATE LOOKUP, which is
// why this project targets net10.0: StringComparer.Ordinal implements
// IAlternateEqualityComparer<ReadOnlySpan<char>, string>, so the set can be
// probed with a span directly and only materializes a string on a genuine
// miss. This class first shipped as a hand-rolled open-addressed string[]
// specifically because that API is net9.0+ and the project was on net8.0 -
// probing a plain Dictionary/HashSet required already having the string, i.e.
// allocating the very thing this type exists to avoid. The retarget removed
// that constraint, so the hand-rolled probing/growth/rehash is gone in favour
// of the BCL's own.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class Utf16StringPool
{
    // Sized for the "few dozen distinct names" case so a normal capture never
    // rehashes at all.
    private const int InitialCapacity = 256;

    private readonly HashSet<string> pooled = new HashSet<string>(InitialCapacity, StringComparer.Ordinal);

    // Held rather than re-acquired per call: GetAlternateLookup validates the
    // comparer supports the alternate type each time it's called, and this is
    // on a per-event path.
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> spanLookup;

    // Distinct strings this pool has materialized - one per genuinely new
    // content, and the number this type exists to keep small.
    public int Count => this.pooled.Count;

    public Utf16StringPool()
    {
        this.spanLookup = this.pooled.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public string GetOrAdd(ReadOnlySpan<char> chars)
    {
        if (chars.Length == 0)
        {
            return string.Empty;
        }

        string existing;
        if (this.spanLookup.TryGetValue(chars, out existing))
        {
            return existing;
        }

        // The one allocation this whole type is built around - reached once
        // per DISTINCT content, never once per event.
        string created = new string(chars);
        this.pooled.Add(created);
        return created;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
