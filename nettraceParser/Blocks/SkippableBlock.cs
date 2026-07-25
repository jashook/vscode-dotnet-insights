////////////////////////////////////////////////////////////////////////////////
// Module: SkippableBlock.cs
//
// Notes:
// Fallback block reader for any Block type name we don't decode yet (e.g.
// StackBlock, SPBlock). Supplied via Deserializer.OnUnregisteredType so the
// container layer never has to know the full set of block types up front -
// adding a new decoded block type later is just registering a new factory,
// not touching this layer.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SkippableBlock : IFastSerializable, IFastSerializableVersion
{
    // We never look at block-type-specific fields, only the leading BlockSize
    // every block shares - so we can honestly claim to support any version.
    public int Version => int.MaxValue;
    public int MinimumVersionCanRead => 0;
    public int MinimumReaderVersion => 0;

    public void FromStream(Deserializer deserializer)
    {
        int blockSize;
        deserializer.Read(out blockSize);

        NettraceBlockAlignment.SkipPaddingToFourByteAlignment(deserializer);

        long targetPosition = (long)deserializer.Current + blockSize;
        deserializer.Reader.Goto((StreamLabel)targetPosition);
    }

    public void ToStream(Serializer serializer)
    {
        throw new System.NotImplementedException("nettraceParser is read-only.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
