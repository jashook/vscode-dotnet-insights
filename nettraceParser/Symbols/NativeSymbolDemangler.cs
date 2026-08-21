////////////////////////////////////////////////////////////////////////////////
// Module: NativeSymbolDemangler.cs
//
// Notes:
// Turns an Itanium C++ ABI mangled name into the qualified function name a
// profile should show: `_ZN3SVR7gc_heap16background_sweepEv` becomes
// `SVR::gc_heap::background_sweep`.
//
// Native symbols out of a symbol server arrive mangled, and a CPU view whose
// hottest rows read `_ZN12ObjectNative25Monitor_TryEnter_FastPathEP6Object` is
// only marginally better than the hex offset it replaced. On the reference
// capture 228 of 3,198 resolved names are mangled, and they include most of
// the runtime's GC and locking internals - exactly the rows somebody is
// reading this view to find.
//
// DELIBERATELY A SUBSET, not a full demangler. The Itanium grammar is large
// (templates, substitutions, expressions, vendor extensions) and a complete
// implementation is a project of its own. What is implemented is the shape
// that essentially all ordinary function symbols take - an optional linkage
// or CV prefix, a run of length-prefixed name components, and a parameter
// list - which covers 226 of those 228 names. Anything else is returned
// UNCHANGED rather than half-decoded: a raw mangled name is honest and
// searchable, while a mangled name that has been partially rewritten is
// neither. This is the same rule UniversalSymbolTable.FormatSymbolName
// follows for CLR names, and for the same reason.
//
// The parameter list is dropped on purpose. Every other method name in this
// project renders without one, the ranked tables split on the last separator
// to dim the qualifying prefix, and two overloads of a hot function are far
// more useful merged into one row than split by signature.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class NativeSymbolDemangler
{
    // A mangled name is at least "_Z" plus one length-prefixed component.
    private const int MinimumMangledLength = 4;

    public static bool IsMangled(string name)
    {
        return name != null && name.Length >= MinimumMangledLength && name[0] == '_' && name[1] == 'Z';
    }

    // Returns the demangled qualified name, or the input unchanged when this
    // subset cannot decode it.
    public static string Demangle(string mangledName)
    {
        if (!IsMangled(mangledName))
        {
            return mangledName;
        }

        int position = 2;

        // `_ZZ...` is an entity local to a function (a static local, a lambda).
        // Its real name needs the enclosing function decoded too, which is
        // past this subset.
        if (mangledName[position] == 'Z')
        {
            return mangledName;
        }

        // `_ZL` marks internal linkage and says nothing about the name.
        if (mangledName[position] == 'L')
        {
            ++position;
        }

        if (position >= mangledName.Length)
        {
            return mangledName;
        }

        if (mangledName[position] != 'N')
        {
            // Unnested: a single component, then the parameter list.
            string singleComponent;

            if (!TryReadLengthPrefixedComponent(mangledName, ref position, out singleComponent))
            {
                return mangledName;
            }

            return singleComponent;
        }

        ++position;

        // CV and ref qualifiers on a member function sit between the `N` and
        // the first component: `_ZNK` is a const member.
        while (position < mangledName.Length && IsQualifier(mangledName[position]))
        {
            ++position;
        }

        StringBuilder qualifiedName = new StringBuilder();

        while (position < mangledName.Length && mangledName[position] != 'E')
        {
            string component;

            if (!TryReadLengthPrefixedComponent(mangledName, ref position, out component))
            {
                // A template argument list, a substitution, or an operator
                // name - none of which this subset decodes.
                return mangledName;
            }

            if (qualifiedName.Length > 0)
            {
                qualifiedName.Append("::");
            }

            qualifiedName.Append(component);
        }

        // A nested name must terminate with 'E'. Running off the end means
        // this was not the shape it looked like.
        if (position >= mangledName.Length || qualifiedName.Length == 0)
        {
            return mangledName;
        }

        return qualifiedName.ToString();
    }

    private static bool IsQualifier(char value)
    {
        // r = restrict, V = volatile, K = const, R = lvalue ref, O = rvalue ref.
        return value == 'r' || value == 'V' || value == 'K' || value == 'R' || value == 'O';
    }

    private static bool TryReadLengthPrefixedComponent(string mangledName, ref int position, out string component)
    {
        component = null;

        if (position >= mangledName.Length || !char.IsDigit(mangledName[position]))
        {
            return false;
        }

        int length = 0;

        while (position < mangledName.Length && char.IsDigit(mangledName[position]))
        {
            // A length that cannot index the string is corrupt, and continuing
            // to accumulate would overflow.
            if (length > mangledName.Length)
            {
                return false;
            }

            length = (length * 10) + (mangledName[position] - '0');
            ++position;
        }

        if (length <= 0 || position + length > mangledName.Length)
        {
            return false;
        }

        component = mangledName.Substring(position, length);
        position += length;
        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
