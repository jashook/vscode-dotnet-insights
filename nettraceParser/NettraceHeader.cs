////////////////////////////////////////////////////////////////////////////////
// Module: NettraceHeader.cs
//
// Notes:
// The root "Trace" object of a .nettrace file (NetTraceFormat_v5.md). Its own
// fields are the trace-wide header (sync timestamp, QPC frequency, pointer
// size, ...). Reading the trailing sequence of Block objects is not this
// class's concern - it happens automatically inside Deserializer.GetEntryObject()
// once NettraceFile sets allowLazyDeserialization = false (see NettraceFile.cs).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class NettraceHeader : IFastSerializable, IFastSerializableVersion
{
    // The Trace object is documented (NetTraceFormat_v5.md) as Version 4 /
    // MinumumReaderVersion 4; FromStream below reads exactly that V3-V5 field
    // layout (EventPipeEventSource.cs's ReadTraceObjectV3To5).
    public int Version => 4;
    public int MinimumVersionCanRead => 4;
    public int MinimumReaderVersion => 4;

    public short Year { get; set; }
    public short Month { get; set; }
    public short DayOfWeek { get; set; }
    public short Day { get; set; }
    public short Hour { get; set; }
    public short Minute { get; set; }
    public short Second { get; set; }
    public short Millisecond { get; set; }
    public long SyncTimeQPC { get; set; }
    public long QPCFrequency { get; set; }
    public int PointerSize { get; set; }
    public int ProcessId { get; set; }
    public int NumberOfProcessors { get; set; }
    public int ExpectedCPUSamplingRate { get; set; }

    public void FromStream(Deserializer deserializer)
    {
        short year;
        short month;
        short dayOfWeek;
        short day;
        short hour;
        short minute;
        short second;
        short millisecond;

        deserializer.Read(out year);
        deserializer.Read(out month);
        deserializer.Read(out dayOfWeek);
        deserializer.Read(out day);
        deserializer.Read(out hour);
        deserializer.Read(out minute);
        deserializer.Read(out second);
        deserializer.Read(out millisecond);

        this.Year = year;
        this.Month = month;
        this.DayOfWeek = dayOfWeek;
        this.Day = day;
        this.Hour = hour;
        this.Minute = minute;
        this.Second = second;
        this.Millisecond = millisecond;

        long syncTimeQPC;
        long qpcFrequency;
        int pointerSize;
        int processId;
        int numberOfProcessors;
        int expectedCPUSamplingRate;

        deserializer.Read(out syncTimeQPC);
        deserializer.Read(out qpcFrequency);
        deserializer.Read(out pointerSize);
        deserializer.Read(out processId);
        deserializer.Read(out numberOfProcessors);
        deserializer.Read(out expectedCPUSamplingRate);

        this.SyncTimeQPC = syncTimeQPC;
        this.QPCFrequency = qpcFrequency;
        this.PointerSize = pointerSize;
        this.ProcessId = processId;
        this.NumberOfProcessors = numberOfProcessors;
        this.ExpectedCPUSamplingRate = expectedCPUSamplingRate;
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
