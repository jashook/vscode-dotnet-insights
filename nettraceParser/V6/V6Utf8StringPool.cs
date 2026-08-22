////////////////////////////////////////////////////////////////////////////////
// Module: V6Utf8StringPool.cs
//
// Notes:
// v6 encodes every string as UTF-8 where v5 used UTF-16 (NetTraceFormat.md,
// "Strings in the metadata are now UTF8 rather than UTF16"). Utf16StringPool
// exists precisely because the v5 wire format is ALREADY UTF-16 and can
// therefore be probed with a ReadOnlySpan<char> view over the payload bytes
// without decoding anything - that property does not survive the switch, so
// v6 has to transcode before it can pool.
//
// The transcode goes into a reusable char[] field rather than a stackalloc:
// every caller here decodes inside a loop (metadata rows, symbol names, one
// per event in the Universal.System case), and this codebase's own rule is
// never to stackalloc inside a loop. One grow-on-demand buffer costs a single
// allocation for the whole parse and keeps the pool hit path allocation-free,
// which is the entire point - a real capture holds ~40 distinct provider/event
// names against millions of events, and 10,187 ProcessSymbol names against
// far more references to them.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class V6Utf8StringPool
{
    // Comfortably covers every name seen in a real capture (the longest
    // observed is a ~90-character generic method signature) while staying
    // small enough that the initial allocation is irrelevant. Grows rather
    // than truncating if a capture proves otherwise.
    private const int InitialCharCapacity = 512;

    private readonly Utf16StringPool pool = new Utf16StringPool();
    private char[] scratch = new char[InitialCharCapacity];

    public int Count => this.pool.Count;

    // Pre-adds a literal so that decoding the same text later returns THIS
    // instance rather than a fresh one. That matters well beyond tidiness:
    // every projector filters with `record.ProviderName != SomeLiteral`, and
    // String.Equals short-circuits on REFERENCE equality - so a decoded
    // instance that merely equals the literal loses that fast path and
    // content-compares once per event per pass. On the v5 path that was
    // measured at 4.9% of a whole run (see CLAUDE.md's note on interning
    // provider/event names at metadata-parse time); seeding here buys the
    // same property for v6, where the pool would otherwise canonicalize on
    // whichever decoded instance happened to arrive first.
    public string Seed(string value)
    {
        return this.pool.GetOrAdd(value.AsSpan());
    }

    public string GetOrAdd(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.Length == 0)
        {
            return string.Empty;
        }

        // One UTF-8 byte can never produce more than one UTF-16 code unit for
        // any single-byte sequence, and multi-byte sequences produce fewer
        // code units than bytes except for the surrogate-pair case, which is
        // 4 bytes -> 2 chars. So byte count is always a safe upper bound.
        if (this.scratch.Length < utf8Bytes.Length)
        {
            int grownCapacity = this.scratch.Length;

            while (grownCapacity < utf8Bytes.Length)
            {
                grownCapacity *= 2;
            }

            this.scratch = new char[grownCapacity];
        }

        int charCount = Encoding.UTF8.GetChars(utf8Bytes, this.scratch);
        return this.pool.GetOrAdd(new ReadOnlySpan<char>(this.scratch, 0, charCount));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
