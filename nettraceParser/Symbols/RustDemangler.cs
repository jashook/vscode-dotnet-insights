////////////////////////////////////////////////////////////////////////////////
// Module: RustDemangler.cs
//
// Notes:
// Demangles Rust symbol names. Rust has TWO schemes and a real capture can
// contain both in one process (the crate built by one toolchain, a dependency
// or the prebuilt std by another):
//
//   - legacy: `_ZN13rust_workload13hot_leaf_beta17h0a1b2c3d4e5f6789E`
//     Itanium C++ mangling reused, with a trailing `17h<16 hex>` hash
//     component that disambiguates monomorphizations. It demangles through an
//     ordinary Itanium demangler; the hash is noise and is stripped.
//
//   - v0 (RFC 2603): `_RNvCs1234_13rust_workload13hot_leaf_beta`
//     A different grammar entirely, with its own encoding for paths, generic
//     arguments, impls and back-references. Itanium demanglers do not
//     understand it and do not fail on it either - they simply decline, which
//     is why perf itself prints v0 symbols raw.
//
// That last point is the reason this file exists rather than deferring to the
// existing Itanium demangler: the reference Rust fixture, built by a current
// stable toolchain with no special flags, is entirely v0, and `perf script`
// renders every one of its frames mangled.
//
// The rule on failure is the one nettraceParser/Symbols/NativeSymbolDemangler.cs
// already follows and for the same reason: anything not fully understood is
// returned UNCHANGED. A raw mangled name is honest and can be pasted into
// `rustfilt`; a half-rewritten one is neither, and is worse than no
// demangling at all because it cannot be recognised as such.
//
// Back-references are the one part that cannot be skipped without breaking
// ordinary names. v0 encodes a repeated path or type as an offset back into
// the same string, and generic-heavy Rust is full of them - a demangler that
// ignores them produces truncated names for exactly the monomorphized
// functions that dominate a Rust profile.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class RustDemangler
{
    // Guards against a pathological or hostile symbol driving unbounded work:
    // back-references can point backwards repeatedly, and a cycle would
    // otherwise not terminate.
    private const int MaxRecursionDepth = 96;
    private const int MaxOutputLength = 4096;

    public static bool IsRustV0(string name)
    {
        return name != null && name.Length > 2 && name[0] == '_' && name[1] == 'R';
    }

    public static bool IsRustLegacy(string name)
    {
        // `_ZN...17h<16 hex>E`. The hash component is what distinguishes a
        // Rust legacy symbol from an ordinary C++ one; without it this would
        // claim every C++ symbol in the process.
        if (name == null || !name.StartsWith("_ZN", StringComparison.Ordinal) || !name.EndsWith("E", StringComparison.Ordinal))
        {
            return false;
        }

        return FindLegacyHashStart(name) >= 0;
    }

    // Returns the demangled name, or the input unchanged when it is not a Rust
    // symbol or cannot be fully decoded.
    public static string Demangle(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (IsRustV0(name))
        {
            string demangled = DemangleV0(name);
            return demangled ?? name;
        }

        if (IsRustLegacy(name))
        {
            string demangled = DemangleLegacy(name);
            return demangled ?? name;
        }

        return name;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Legacy
    ////////////////////////////////////////////////////////////////////////////

    // `_ZN` <len> <component> ... `17h` <16 hex> `E`
    private static string DemangleLegacy(string name)
    {
        int hashStart = FindLegacyHashStart(name);
        if (hashStart < 0)
        {
            return null;
        }

        StringBuilder output = new StringBuilder();
        int position = 3;

        while (position < hashStart)
        {
            int componentLength = 0;
            int digitStart = position;

            while (position < hashStart && name[position] >= '0' && name[position] <= '9')
            {
                componentLength = (componentLength * 10) + (name[position] - '0');
                ++position;
            }

            if (position == digitStart || componentLength <= 0 || position + componentLength > hashStart)
            {
                return null;
            }

            if (output.Length > 0)
            {
                output.Append("::");
            }

            AppendLegacyComponent(output, name, position, componentLength);
            position += componentLength;
        }

        if (output.Length == 0)
        {
            return null;
        }

        return output.ToString();
    }

    // The `17h<16 hex>` component immediately before the trailing `E`.
    private static int FindLegacyHashStart(string name)
    {
        // 3 for "17h" plus 16 hex digits plus the trailing "E".
        int candidate = name.Length - 1 - 16 - 3;
        if (candidate < 3)
        {
            return -1;
        }

        if (name[candidate] != '1' || name[candidate + 1] != '7' || name[candidate + 2] != 'h')
        {
            return -1;
        }

        for (int hexIndex = candidate + 3; hexIndex < name.Length - 1; ++hexIndex)
        {
            if (!IsHexDigit(name[hexIndex]))
            {
                return -1;
            }
        }

        return candidate;
    }

    // Legacy mangling escapes characters that Itanium cannot carry: `$LT$` for
    // '<', `$u20$` for a space, `..` for `::`, and so on. Leaving them encoded
    // makes a generic name unreadable.
    private static void AppendLegacyComponent(StringBuilder output, string name, int start, int length)
    {
        int position = start;
        int end = start + length;

        while (position < end)
        {
            char current = name[position];

            if (current == '.' && position + 1 < end && name[position + 1] == '.')
            {
                output.Append("::");
                position += 2;
                continue;
            }

            if (current != '$')
            {
                output.Append(current);
                ++position;
                continue;
            }

            int closing = name.IndexOf('$', position + 1);
            if (closing < 0 || closing >= end)
            {
                output.Append(current);
                ++position;
                continue;
            }

            string escape = name.Substring(position + 1, closing - position - 1);
            string replacement = TranslateLegacyEscape(escape);

            if (replacement == null)
            {
                output.Append(current);
                ++position;
                continue;
            }

            output.Append(replacement);
            position = closing + 1;
        }
    }

    private static string TranslateLegacyEscape(string escape)
    {
        switch (escape)
        {
            case "SP": return "@";
            case "BP": return "*";
            case "RF": return "&";
            case "LT": return "<";
            case "GT": return ">";
            case "LP": return "(";
            case "RP": return ")";
            case "C": return ",";
            default: break;
        }

        // `u<hex>` - a raw code point.
        if (escape.Length > 1 && escape[0] == 'u')
        {
            int codePoint = 0;
            for (int hexIndex = 1; hexIndex < escape.Length; ++hexIndex)
            {
                int digit = HexValue(escape[hexIndex]);
                if (digit < 0)
                {
                    return null;
                }

                codePoint = (codePoint * 16) + digit;
            }

            if (codePoint > 0 && codePoint <= 0x10FFFF)
            {
                return char.ConvertFromUtf32(codePoint);
            }
        }

        return null;
    }

    ////////////////////////////////////////////////////////////////////////////
    // v0
    ////////////////////////////////////////////////////////////////////////////

    private static string DemangleV0(string name)
    {
        // Everything after `_R`. All back-reference offsets are relative to
        // this, not to the original string, so slicing here is what makes them
        // meaningful.
        string body = name.Substring(2);

        // <vendor-specific-suffix> = ("." | "$") <suffix>, at the very end, and
        // preserved verbatim - `MAIN.0` and `foo.llvm.7481796317119325043` are
        // DIFFERENT symbols from `MAIN` and `foo`, so dropping the suffix
        // merges rows that are genuinely distinct. Splitting on the first '.'
        // is safe because a v0 identifier is alphanumeric and underscore only;
        // there is no other way for one to appear.
        string vendorSuffix = string.Empty;
        int suffixStart = body.IndexOf('.');
        if (suffixStart >= 0)
        {
            vendorSuffix = body.Substring(suffixStart);
            body = body.Substring(0, suffixStart);
        }

        // `.llvm.<hex>` is not a vendor suffix worth showing - it is LLVM's own
        // uniquing tag for an internalized symbol, and rustc strips it while
        // keeping every other suffix. The distinction matters both ways: `.0`
        // names a genuinely different item and must survive, and two functions
        // differing only by an LLVM tag are the same source function and should
        // rank as one row.
        if (vendorSuffix.StartsWith(".llvm.", StringComparison.Ordinal) && IsLlvmUniquingTag(vendorSuffix.Substring(6)))
        {
            vendorSuffix = string.Empty;
        }

        // An optional encoding-version decimal. Only version 0 (no digit) is
        // defined; anything else is from the future and is declined rather
        // than guessed at.
        if (body.Length > 0 && body[0] >= '0' && body[0] <= '9')
        {
            return null;
        }

        V0Parser parser = new V0Parser(body);
        StringBuilder output = new StringBuilder();

        if (!parser.ParsePath(output, 0, true))
        {
            return null;
        }

        if (output.Length == 0)
        {
            return null;
        }

        output.Append(vendorSuffix);
        return output.ToString();
    }

    private sealed class V0Parser
    {
        private readonly string source;
        private int position;

        // How many lifetimes are currently bound by enclosing `for<...>`
        // binders. A lifetime is encoded as a de Bruijn index INTO this, so
        // the same encoded index means a different lifetime depending on where
        // it appears - which is why it cannot be rendered without carrying
        // this along.
        private int boundLifetimeDepth;

        public V0Parser(string source)
        {
            this.source = source;
            this.position = 0;
            this.boundLifetimeDepth = 0;
        }

        private bool AtEnd => this.position >= this.source.Length;

        private char Peek()
        {
            return this.AtEnd ? '\0' : this.source[this.position];
        }

        private char Next()
        {
            if (this.AtEnd)
            {
                return '\0';
            }

            char value = this.source[this.position];
            ++this.position;
            return value;
        }

        // <base-62-number> = { 0-9 a-z A-Z } "_"; empty means 0, otherwise the
        // parsed value plus one.
        //
        // Accumulated as ULONG, unchecked. These are u64 values and a crate
        // disambiguator really does use the full width - the reference
        // fixture's own crate hash is `sfsN783Mr1pU_`, eleven base-62 digits.
        // An earlier version accumulated into a long and rejected on
        // overflowing Int64.MaxValue, which silently declined EVERY symbol in
        // the binary: the crate root is the first thing every path resolves
        // to, so one unrepresentable hash loses the whole capture rather than
        // one name. Nothing here needs the value to be arithmetically
        // meaningful - it is a discarded disambiguator, a bounds-checked
        // back-reference offset, or a closure index - so wrapping is
        // preferable to failing.
        private bool TryParseBase62(out ulong value)
        {
            value = 0;

            if (this.Peek() == '_')
            {
                ++this.position;
                return true;
            }

            ulong accumulated = 0;
            bool sawDigit = false;

            while (!this.AtEnd)
            {
                char current = this.source[this.position];
                int digit;

                if (current >= '0' && current <= '9')
                {
                    digit = current - '0';
                }
                else if (current >= 'a' && current <= 'z')
                {
                    digit = 10 + (current - 'a');
                }
                else if (current >= 'A' && current <= 'Z')
                {
                    digit = 36 + (current - 'A');
                }
                else
                {
                    break;
                }

                accumulated = unchecked((accumulated * 62) + (ulong)digit);
                sawDigit = true;
                ++this.position;
            }

            if (!sawDigit || this.Next() != '_')
            {
                return false;
            }

            value = unchecked(accumulated + 1);
            return true;
        }

        // <decimal-number> = "0" | [1-9] {digit}. A LEADING ZERO IS THE WHOLE
        // NUMBER - there are no leading zeros in this grammar, so "000" is
        // three separate zero-length identifiers, not one number.
        //
        // Parsing it greedily instead consumes all three and leaves the cursor
        // in the middle of the next construct. It shows up on nested anonymous
        // items, which is where zero-length identifiers cluster: a chain like
        // `::{closure#1}::{closure#0}::{closure#0}` encodes as `s_000`, and
        // greedy parsing turned that into one identifier and then failed on
        // the remainder.
        private bool TryParseDecimal(out int value)
        {
            value = 0;

            if (this.Peek() == '0')
            {
                ++this.position;
                return true;
            }

            bool sawDigit = false;

            while (!this.AtEnd)
            {
                char current = this.source[this.position];
                if (current < '0' || current > '9')
                {
                    break;
                }

                if (value > (int.MaxValue - (current - '0')) / 10)
                {
                    return false;
                }

                value = (value * 10) + (current - '0');
                sawDigit = true;
                ++this.position;
            }

            return sawDigit;
        }

        // <disambiguator> = "s" <base-62-number>. Distinguishes two items that
        // would otherwise have the same path - two closures in one function,
        // two impls of the same shape. Discarded for ordinary named items, but
        // it IS the N rendered in `{closure#N}`, which is the only name an
        // anonymous item has.
        //
        // Its value is the base-62 number PLUS ONE, and absence means zero -
        // verified against rustc's own output across six symbols in the
        // fixture: `s_` renders #1, `s0_` renders #2, `s4_` renders #6, and no
        // disambiguator at all renders #0.
        private bool TryParseDisambiguator(out ulong disambiguator)
        {
            disambiguator = 0;

            if (this.Peek() != 's')
            {
                return true;
            }

            ++this.position;

            ulong parsed;
            if (!this.TryParseBase62(out parsed))
            {
                return false;
            }

            disambiguator = unchecked(parsed + 1);
            return true;
        }

        // <undisambiguated-identifier> = ["u"] <decimal-number> ["_"] <bytes>
        private bool TryParseIdentifier(out string identifier)
        {
            identifier = null;

            bool isPunycode = false;
            if (this.Peek() == 'u')
            {
                isPunycode = true;
                ++this.position;
            }

            int length;
            if (!this.TryParseDecimal(out length))
            {
                return false;
            }

            // The separator is present only when the name would otherwise be
            // ambiguous with the length digits - i.e. when it starts with '_'
            // or a digit.
            if (this.Peek() == '_')
            {
                ++this.position;
            }

            if (length < 0 || this.position + length > this.source.Length)
            {
                return false;
            }

            identifier = this.source.Substring(this.position, length);
            this.position += length;

            if (isPunycode)
            {
                // Punycode identifiers are legal but vanishingly rare in
                // compiled code. Declining is better than emitting a name that
                // silently differs from the source.
                return false;
            }

            return true;
        }

        // `L0` is the anonymous lifetime; anything else counts outwards
        // through the enclosing binders. Named 'a, 'b, ... from the OUTERMOST
        // binder inwards, matching how the source would have written them.
        private bool PrintLifetime(StringBuilder output, ulong index)
        {
            if (index == 0)
            {
                output.Append("'_");
                return true;
            }

            if (index > (ulong)this.boundLifetimeDepth)
            {
                return false;
            }

            int depthFromOutermost = this.boundLifetimeDepth - (int)index;

            output.Append('\'');
            output.Append((char)('a' + (depthFromOutermost % 26)));

            if (depthFromOutermost >= 26)
            {
                output.Append(depthFromOutermost / 26);
            }

            return true;
        }

        // <binder> = "G" <base-62-number>, introducing that many higher-ranked
        // lifetimes. Rendered as `for<'a, 'b> `, and left in place afterwards -
        // the caller must restore boundLifetimeDepth once the bound construct
        // ends, or a later lifetime resolves against a binder that is no
        // longer in scope.
        private bool TryParseBinder(StringBuilder output, out int introducedLifetimes)
        {
            introducedLifetimes = 0;

            if (this.Peek() != 'G')
            {
                return true;
            }

            ++this.position;

            ulong lifetimeCount;
            if (!this.TryParseBase62(out lifetimeCount))
            {
                return false;
            }

            // PLUS ONE, the same off-by-one the disambiguator carries: rustc
            // reads both through one `opt_integer_62` helper, which returns 0
            // when the tag is absent and integer_62 + 1 when it is present.
            // Without it `DG0_` introduces one lifetime instead of two, and
            // every de Bruijn index inside the binder then resolves one slot
            // too far out - `for<'a, 'b> Fn(&'a T<'b>)` came out as a decline
            // because the outermost index no longer had a binder to land in.
            lifetimeCount = unchecked(lifetimeCount + 1);

            if (lifetimeCount > (ulong)MaxRecursionDepth)
            {
                return false;
            }

            introducedLifetimes = (int)lifetimeCount;

            output.Append("for<");
            for (int lifetimeIndex = 0; lifetimeIndex < introducedLifetimes; ++lifetimeIndex)
            {
                if (lifetimeIndex > 0)
                {
                    output.Append(", ");
                }

                ++this.boundLifetimeDepth;

                // Always the innermost binding at this point in the loop.
                if (!this.PrintLifetime(output, 1))
                {
                    return false;
                }
            }

            output.Append("> ");
            return true;
        }

        public bool ParsePath(StringBuilder output, int depth, bool isValuePath)
        {
            bool ignoredLeftOpen;
            return this.ParsePathCore(output, depth, isValuePath, false, out ignoredLeftOpen);
        }

        // A `dyn Trait` with associated bindings needs the trait's own generic
        // argument list left OPEN so the bindings can be appended into it:
        // rustc renders `dyn FnOnce<(), Output = ()>`, one list, not
        // `dyn FnOnce<()><Output = ()>`, which is what closing it early
        // produces and is not valid Rust in any position.
        private bool ParsePathMaybeOpenGenerics(StringBuilder output, int depth, out bool leftOpen)
        {
            return this.ParsePathCore(output, depth, false, true, out leftOpen);
        }

        private bool ParsePathCore(StringBuilder output, int depth, bool isValuePath, bool allowOpenGenerics, out bool leftOpen)
        {
            leftOpen = false;

            if (depth > MaxRecursionDepth || output.Length > MaxOutputLength || this.AtEnd)
            {
                return false;
            }

            char tag = this.Next();

            switch (tag)
            {
                case 'C':
                {
                    // Crate root.
                    ulong crateDisambiguator;
                    if (!this.TryParseDisambiguator(out crateDisambiguator))
                    {
                        return false;
                    }

                    string crateName;
                    if (!this.TryParseIdentifier(out crateName))
                    {
                        return false;
                    }

                    output.Append(crateName);
                    return true;
                }

                case 'N':
                {
                    // Nested: <namespace> <parent path> <identifier>
                    char namespaceTag = this.Next();
                    if (namespaceTag == '\0')
                    {
                        return false;
                    }

                    if (!this.ParsePath(output, depth + 1, isValuePath))
                    {
                        return false;
                    }

                    ulong componentDisambiguator;
                    if (!this.TryParseDisambiguator(out componentDisambiguator))
                    {
                        return false;
                    }

                    string componentName;
                    if (!this.TryParseIdentifier(out componentName))
                    {
                        return false;
                    }

                    if (namespaceTag >= 'a' && namespaceTag <= 'z')
                    {
                        // An ordinary namespace (type, value, ...): the
                        // component is just another path segment.
                        output.Append("::");
                        output.Append(componentName);
                        return true;
                    }

                    // An internal namespace - a closure, a shim, anything with
                    // no source-level name. Rendered exactly as rustc renders
                    // it, `::{closure#0}`, because that is the form that
                    // matches a Rust backtrace and a `perf`/`rustfilt` output
                    // the reader may be holding beside this one.
                    //
                    // SINGLE braces with the disambiguator index. The doubled
                    // `{{closure}}` form belongs to the LEGACY scheme and is
                    // what an earlier version emitted here; it reads as
                    // correct, and turns every distinct closure in a hot
                    // module into the same indistinguishable row.
                    output.Append("::{");

                    if (namespaceTag == 'C')
                    {
                        output.Append("closure");
                    }
                    else if (namespaceTag == 'S')
                    {
                        output.Append("shim");
                    }
                    else
                    {
                        output.Append(namespaceTag);
                    }

                    if (componentName.Length > 0)
                    {
                        output.Append(':');
                        output.Append(componentName);
                    }

                    output.Append('#');
                    output.Append(componentDisambiguator);
                    output.Append('}');
                    return true;
                }

                case 'M':
                {
                    // Inherent impl: <impl-path> <type> rendered as <Type>.
                    ulong inherentImplDisambiguator;
                    if (!this.TryParseDisambiguator(out inherentImplDisambiguator))
                    {
                        return false;
                    }

                    // The impl path names the module the impl is written in.
                    // It is parsed to advance the cursor (and to register the
                    // right back-reference offsets) but is not rendered -
                    // rustc does not render it either.
                    StringBuilder discarded = new StringBuilder();
                    if (!this.ParsePath(discarded, depth + 1, false))
                    {
                        return false;
                    }

                    output.Append('<');
                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append('>');
                    return true;
                }

                case 'X':
                {
                    // Trait impl: <impl-path> <type> <trait path>
                    ulong traitImplDisambiguator;
                    if (!this.TryParseDisambiguator(out traitImplDisambiguator))
                    {
                        return false;
                    }

                    StringBuilder discarded = new StringBuilder();
                    if (!this.ParsePath(discarded, depth + 1, false))
                    {
                        return false;
                    }

                    output.Append('<');
                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append(" as ");
                    if (!this.ParsePath(output, depth + 1, false))
                    {
                        return false;
                    }

                    output.Append('>');
                    return true;
                }

                case 'Y':
                {
                    // Trait definition: <type> <trait path>
                    output.Append('<');
                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append(" as ");
                    if (!this.ParsePath(output, depth + 1, false))
                    {
                        return false;
                    }

                    output.Append('>');
                    return true;
                }

                case 'I':
                {
                    // Generic instantiation: <path> {<generic-arg>} "E"
                    if (!this.ParsePath(output, depth + 1, isValuePath))
                    {
                        return false;
                    }

                    // A value path takes the turbofish, matching how it would
                    // be written in source.
                    output.Append(isValuePath ? "::<" : "<");

                    bool firstArgument = true;
                    while (this.Peek() != 'E')
                    {
                        if (this.AtEnd)
                        {
                            return false;
                        }

                        if (!firstArgument)
                        {
                            output.Append(", ");
                        }

                        firstArgument = false;

                        if (!this.ParseGenericArgument(output, depth + 1))
                        {
                            return false;
                        }
                    }

                    // Consume the terminating 'E'.
                    ++this.position;

                    if (allowOpenGenerics)
                    {
                        leftOpen = true;
                        return true;
                    }

                    output.Append('>');
                    return true;
                }

                case 'B':
                {
                    return this.ParseBackReference(output, depth, true, isValuePath, allowOpenGenerics, out leftOpen);
                }

                default:
                    return false;
            }
        }

        private bool ParseGenericArgument(StringBuilder output, int depth)
        {
            char tag = this.Peek();

            if (tag == 'L')
            {
                ++this.position;

                ulong lifetimeIndex;
                if (!this.TryParseBase62(out lifetimeIndex))
                {
                    return false;
                }

                return this.PrintLifetime(output, lifetimeIndex);
            }

            if (tag == 'K')
            {
                ++this.position;
                return this.ParseConst(output, depth + 1);
            }

            return this.ParseType(output, depth + 1);
        }

        private bool ParseConst(StringBuilder output, int depth)
        {
            if (depth > MaxRecursionDepth || this.AtEnd)
            {
                return false;
            }

            char tag = this.Peek();

            if (tag == 'p')
            {
                ++this.position;
                output.Append('_');
                return true;
            }

            if (tag == 'B')
            {
                ++this.position;
                return this.ParseBackReference(output, depth, false, false);
            }

            // <const> = <type> <const-data>. The type is consumed but not
            // rendered - `foo::<3>` reads better than `foo::<3usize>` and is
            // what rustc prints.
            StringBuilder discardedType = new StringBuilder();
            if (!this.ParseType(discardedType, depth + 1))
            {
                return false;
            }

            bool isNegative = false;
            if (this.Peek() == 'n')
            {
                isNegative = true;
                ++this.position;
            }

            StringBuilder hexDigits = new StringBuilder();
            while (!this.AtEnd && this.Peek() != '_')
            {
                char current = this.Next();
                if (!IsHexDigit(current))
                {
                    return false;
                }

                hexDigits.Append(current);
            }

            if (this.Next() != '_')
            {
                return false;
            }

            if (hexDigits.Length == 0)
            {
                output.Append('0');
                return true;
            }

            ulong value;
            if (!ulong.TryParse(hexDigits.ToString(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                // Wider than 64 bits - rendered as its hex form rather than
                // declined, since the alternative is losing the whole symbol
                // over a const parameter.
                output.Append("0x");
                output.Append(hexDigits);
                return true;
            }

            if (isNegative)
            {
                output.Append('-');
            }

            output.Append(value);
            return true;
        }

        private bool ParseType(StringBuilder output, int depth)
        {
            if (depth > MaxRecursionDepth || output.Length > MaxOutputLength || this.AtEnd)
            {
                return false;
            }

            char tag = this.Peek();

            string basicType = BasicTypeName(tag);
            if (basicType != null)
            {
                ++this.position;
                output.Append(basicType);
                return true;
            }

            switch (tag)
            {
                case 'A':
                {
                    // [T; N]
                    ++this.position;
                    output.Append('[');
                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append("; ");
                    if (!this.ParseConst(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append(']');
                    return true;
                }

                case 'S':
                {
                    // [T]
                    ++this.position;
                    output.Append('[');
                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }

                    output.Append(']');
                    return true;
                }

                case 'T':
                {
                    // Tuple. A one-element tuple keeps its trailing comma, as
                    // in source.
                    ++this.position;
                    output.Append('(');

                    int elementCount = 0;
                    while (this.Peek() != 'E')
                    {
                        if (this.AtEnd)
                        {
                            return false;
                        }

                        if (elementCount > 0)
                        {
                            output.Append(", ");
                        }

                        if (!this.ParseType(output, depth + 1))
                        {
                            return false;
                        }

                        ++elementCount;
                    }

                    ++this.position;

                    if (elementCount == 1)
                    {
                        output.Append(',');
                    }

                    output.Append(')');
                    return true;
                }

                case 'R':
                case 'Q':
                {
                    ++this.position;
                    output.Append('&');

                    if (this.Peek() == 'L')
                    {
                        ++this.position;

                        ulong lifetimeIndex;
                        if (!this.TryParseBase62(out lifetimeIndex))
                        {
                            return false;
                        }

                        StringBuilder lifetime = new StringBuilder();
                        if (!this.PrintLifetime(lifetime, lifetimeIndex))
                        {
                            return false;
                        }

                        // The anonymous lifetime is not written in a reference
                        // type; `&'_ T` is noise where `&T` is what the source
                        // said.
                        if (lifetime.ToString() != "'_")
                        {
                            output.Append(lifetime);
                            output.Append(' ');
                        }
                    }

                    if (tag == 'Q')
                    {
                        output.Append("mut ");
                    }

                    return this.ParseType(output, depth + 1);
                }

                case 'P':
                {
                    ++this.position;
                    output.Append("*const ");
                    return this.ParseType(output, depth + 1);
                }

                case 'O':
                {
                    ++this.position;
                    output.Append("*mut ");
                    return this.ParseType(output, depth + 1);
                }

                case 'F':
                {
                    ++this.position;
                    return this.ParseFnSignature(output, depth + 1);
                }

                case 'D':
                {
                    ++this.position;
                    return this.ParseDynBounds(output, depth + 1);
                }

                case 'B':
                {
                    ++this.position;
                    return this.ParseBackReference(output, depth, false, false);
                }

                default:
                    // Anything else is a path (a user type).
                    return this.ParsePath(output, depth + 1, false);
            }
        }

        private bool ParseFnSignature(StringBuilder output, int depth)
        {
            int introducedLifetimes;
            if (!this.TryParseBinder(output, out introducedLifetimes))
            {
                return false;
            }

            if (this.Peek() == 'U')
            {
                ++this.position;
                output.Append("unsafe ");
            }

            if (this.Peek() == 'K')
            {
                ++this.position;

                string abi;
                if (this.Peek() == 'C')
                {
                    ++this.position;
                    abi = "C";
                }
                else if (!this.TryParseIdentifier(out abi))
                {
                    return false;
                }

                output.Append("extern \"");
                output.Append(abi.Replace('_', '-'));
                output.Append("\" ");
            }

            output.Append("fn(");

            int parameterCount = 0;
            while (this.Peek() != 'E')
            {
                if (this.AtEnd)
                {
                    return false;
                }

                if (parameterCount > 0)
                {
                    output.Append(", ");
                }

                if (!this.ParseType(output, depth + 1))
                {
                    return false;
                }

                ++parameterCount;
            }

            ++this.position;
            output.Append(')');

            StringBuilder returnType = new StringBuilder();
            if (!this.ParseType(returnType, depth + 1))
            {
                return false;
            }

            // `-> ()` is noise on every non-returning function; rustc omits it.
            if (returnType.ToString() != "()")
            {
                output.Append(" -> ");
                output.Append(returnType);
            }

            this.boundLifetimeDepth -= introducedLifetimes;
            return true;
        }

        private bool ParseDynBounds(StringBuilder output, int depth)
        {
            output.Append("dyn ");

            // The binder is rendered INSIDE `dyn `, as `dyn for<'a> Trait`.
            int introducedLifetimes;
            if (!this.TryParseBinder(output, out introducedLifetimes))
            {
                return false;
            }

            int traitCount = 0;
            while (this.Peek() != 'E')
            {
                if (this.AtEnd)
                {
                    return false;
                }

                if (traitCount > 0)
                {
                    output.Append(" + ");
                }

                bool genericsLeftOpen;
                if (!this.ParsePathMaybeOpenGenerics(output, depth + 1, out genericsLeftOpen))
                {
                    return false;
                }

                // Associated type bindings join the trait's OWN generic list -
                // `dyn FnOnce<(), Output = ()>` - opening one if the trait had
                // no generic arguments of its own.
                while (this.Peek() == 'p')
                {
                    ++this.position;

                    string associatedName;
                    if (!this.TryParseIdentifier(out associatedName))
                    {
                        return false;
                    }

                    if (!genericsLeftOpen)
                    {
                        output.Append('<');
                        genericsLeftOpen = true;
                    }
                    else
                    {
                        output.Append(", ");
                    }

                    output.Append(associatedName);
                    output.Append(" = ");

                    if (!this.ParseType(output, depth + 1))
                    {
                        return false;
                    }
                }

                if (genericsLeftOpen)
                {
                    output.Append('>');
                }

                ++traitCount;
            }

            ++this.position;

            // The trailing lifetime bound. `+ 'static` is written out; the
            // anonymous one is not, matching rustc.
            if (this.Peek() == 'L')
            {
                ++this.position;

                ulong lifetimeIndex;
                if (!this.TryParseBase62(out lifetimeIndex))
                {
                    return false;
                }

                if (lifetimeIndex != 0)
                {
                    output.Append(" + ");
                    if (!this.PrintLifetime(output, lifetimeIndex))
                    {
                        return false;
                    }
                }
            }

            this.boundLifetimeDepth -= introducedLifetimes;
            return true;
        }

        // A back-reference is an offset into this same string. Parsing it means
        // jumping there, parsing whatever is there, and coming back - the
        // cursor must be restored, or everything after the reference decodes
        // from the wrong place.
        private bool ParseBackReference(StringBuilder output, int depth, bool asPath, bool isValuePath)
        {
            bool ignoredLeftOpen;
            return this.ParseBackReference(output, depth, asPath, isValuePath, false, out ignoredLeftOpen);
        }

        private bool ParseBackReference(StringBuilder output, int depth, bool asPath, bool isValuePath, bool allowOpenGenerics, out bool leftOpen)
        {
            leftOpen = false;
            ulong offset;
            if (!this.TryParseBase62(out offset))
            {
                return false;
            }

            if (offset >= (ulong)this.source.Length)
            {
                return false;
            }

            int resumePosition = this.position;

            // Strictly backwards, which is what makes cycles impossible and
            // therefore makes the depth bound a belt-and-braces guard rather
            // than the only thing standing between this and a hang.
            if (offset >= (ulong)resumePosition)
            {
                return false;
            }

            this.position = (int)offset;

            bool parsed = asPath
                ? this.ParsePathCore(output, depth + 1, isValuePath, allowOpenGenerics, out leftOpen)
                : this.ParseType(output, depth + 1);

            this.position = resumePosition;
            return parsed;
        }
    }

    private static string BasicTypeName(char tag)
    {
        switch (tag)
        {
            case 'a': return "i8";
            case 'b': return "bool";
            case 'c': return "char";
            case 'd': return "f64";
            case 'e': return "str";
            case 'f': return "f32";
            case 'h': return "u8";
            case 'i': return "isize";
            case 'j': return "usize";
            case 'l': return "i32";
            case 'm': return "u32";
            case 'n': return "i128";
            case 'o': return "u128";
            case 'p': return "_";
            case 's': return "i16";
            case 't': return "u16";
            case 'u': return "()";
            case 'v': return "...";
            case 'x': return "i64";
            case 'y': return "u64";
            case 'z': return "!";
            default: return null;
        }
    }

    private static bool IsLlvmUniquingTag(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < candidate.Length; ++index)
        {
            char current = candidate[index];
            bool allowed = (current >= '0' && current <= '9')
                || (current >= 'A' && current <= 'F')
                || current == '@';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char value)
    {
        return HexValue(value) >= 0;
    }

    private static int HexValue(char value)
    {
        if (value >= '0' && value <= '9')
        {
            return value - '0';
        }

        if (value >= 'a' && value <= 'f')
        {
            return 10 + (value - 'a');
        }

        if (value >= 'A' && value <= 'F')
        {
            return 10 + (value - 'A');
        }

        return -1;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
