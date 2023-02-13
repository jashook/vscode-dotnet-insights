////////////////////////////////////////////////////////////////////////////////
// Module: ProcessEventCollector.cs
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights;

using System.Text;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;

using Microsoft.Diagnostics.Tracing.Parsers.Clr;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace DniListener {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public struct JitData
{
    public bool Tiered { get; set; }
    public bool Loaded { get; set; }
    public long MethodId { get; set; }
    public int Tier { get; set; }
    public string? Name { get; set; }
    public double Time { get; set; }
}

public struct AllocData
{
    public int HeapId { get; set; }
    public string? Kind { get; set; }
    public string? Type { get; set; }
    public long Size { get; set; }
}

public class ProcessEventCollector
{
    public int ProcessID;
    public bool GcCollect;
    public bool GcAllocCollect;
    public bool ClrThreadCollect;
    public bool CpuSampleCollect;
    public bool JitEventCollect;

    private StreamWriter FileStream;

    public int DurationMs;

    public bool UseStdOut;

    public string OutputFileName;

    private ArrayPool<char> BufferPool;
    private char[] Buffer;

    private int WriteIndex;

    private object WriteLock = new Object();
    
    private JsonSerializerOptions SerializerOptions;

    public ProcessEventCollector(int processId,
                                 bool collectGc,
                                 bool collectGcAllocs,
                                 bool collectClrThreads,
                                 bool collectCpuSample,
                                 bool collectJitEvents,
                                 int durationInMs,
                                 string? outputFileName,
                                 bool writeToStdOut)
    {
        this.WriteIndex = 0;

        this.ProcessID = processId;
        this.GcCollect = collectGc;
        this.GcAllocCollect = collectGcAllocs;
        this.ClrThreadCollect = collectClrThreads;
        this.CpuSampleCollect = collectCpuSample;
        this.JitEventCollect = collectJitEvents;

        this.DurationMs = durationInMs;

        if (string.IsNullOrEmpty(outputFileName))
        {
            DateTime now = DateTime.UtcNow;

            outputFileName = $"dni-listener-collect-{now.Year}-{now.Month}-{now.Day}-{now.Hour}-{now.Minute}-{now.Second}.json";
        }
        
        this.OutputFileName = Path.Combine(Environment.CurrentDirectory, outputFileName);
        this.UseStdOut = writeToStdOut;

        this.FileStream = new(this.OutputFileName, append: true);

        this.BufferPool = ArrayPool<char>.Shared;
        this.Buffer = this.BufferPool.Rent(4096);

        this.FileStream.Write(@"{""data"":[");

        this.SerializerOptions = new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    void JitEventDataCallback(string processId, string processName, MethodJitInfo data)
    {
        JitData writeData = new JitData();
        writeData.Tiered = data.isTieredUp;
        writeData.Loaded = data.HasLoaded;
        writeData.MethodId = data.MethodId;
        writeData.Tier = data.Tier;
        writeData.Name = data.MethodName;
        writeData.Time = data.LoadTime;

        string json = JsonSerializer.Serialize<JitData>(writeData, this.SerializerOptions);
        this.WriteToBuffer(json);
    }

    void GcAllocEventDataCallback(string processId, string processName, AllocationInfo data)
    {
        AllocData writeData = new AllocData();
        writeData.HeapId = data.HeapIndex;
        writeData.Kind = data.Kind == GCAllocationKind.Small ? "small" : "large";
        writeData.Type = data.TypeName;
        writeData.Size = data.AllocSizeBytes;

        string json = JsonSerializer.Serialize<AllocData>(writeData, this.SerializerOptions);
        this.WriteToBuffer(json);
    }
    
    void GcCollectEventDataCallback(string processId, string processName, GcInfo data)
    {
    }

    public void Collect()
    {
        WaitHandle handle = new AutoResetEvent(false);

        var listener = new EventPipeBasedListener(listenForGcData: this.GcCollect, 
                                                  listenForAllocations: this.GcAllocCollect, 
                                                  listenForJitEvents: this.JitEventCollect, 
                                                  jitEventCallback: this.JitEventDataCallback, 
                                                  allocInfoCallback: this.GcAllocEventDataCallback, 
                                                  gcCollectCallback: this.GcCollectEventDataCallback,
                                                  scopedProcessId: this.ProcessID);
        listener.Listen(parkMainThread: false);

        if (this.UseStdOut)
        {
            Console.WriteLine($"Collection Started {DateTime.UtcNow.Date}.");
            Console.WriteLine();
            Console.WriteLine($"    Writing to: {this.OutputFileName}");
            Console.WriteLine();

            StringBuilder sb = new();
            if (this.GcCollect)
            {
                sb.Append("gc-collect-");
            }
            if (this.GcAllocCollect)
            {
                sb.Append("gc-alloc-");
            }
            if (this.ClrThreadCollect)
            {
                sb.Append("threads");
            }
            if (this.JitEventCollect)
            {
                sb.Append("jit-");
            }
            if (this.CpuSampleCollect)
            {
                sb.Append("cpu-sample-");
            }

            string collectionString = sb.ToString();
            collectionString = collectionString.Substring(0, collectionString.Length - 1);

            double tickCount = Math.Floor((double)this.DurationMs / 100.0);
            for (int tick = 0; tick < (int)tickCount + 0; ++tick)
            {
                // Read the size of the output file, output it, then wait for a
                // tick.
                long size = new FileInfo(this.OutputFileName).Length;

                double value = size;

                int minutes = 0;
                int seconds = 0;
                int ms = 0;

                ms = tick * 100;
                seconds = ms / 1000;
                minutes = seconds / 60;

                ms %= 1000;
                seconds %= 60;

                string byteType = "b";
                double mb = size / 1024;

                if (mb > 1)
                {
                    byteType = "mb";
                    value = mb;
                }
                
                Console.Write($"\r    {collectionString}: {value:0.#} {byteType} - Elapsed Time: {minutes}:{seconds}:{ms}");
                ClearBuffer();
                handle.WaitOne(100);
            }

            ClearBuffer();
            this.FileStream.Write("]}");
            this.FileStream.Flush();
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////

    private void ClearBuffer()
    {
        char[] bufferReference;
        int writeCount;

        lock(this.WriteLock)
        {
            if (this.WriteIndex == 0)
            {
                return;
            }

            // Get a new buffer.
            bufferReference = this.Buffer;
            writeCount = this.WriteIndex;
            this.Buffer = this.BufferPool.Rent(4096);
            this.WriteIndex = 0;
        }
        
        // Write the contents in a background thread
        Task.Run(() => {
            this.FileStream.Write(bufferReference, 0, writeCount);
            this.BufferPool.Return(bufferReference);

            this.FileStream.Write(',');
            this.FileStream.Flush();
        });
    }

    private void WriteToBuffer(string data)
    {
        lock(this.WriteLock)
        {
            if (data.Length + this.WriteIndex >= this.Buffer.Length)
            {
                this.ClearBuffer();
            }
            else if (data.Length + this.WriteIndex + data.Length >= this.Buffer.Length)
            {
                this.ClearBuffer();
            }

            data.CopyTo(0, this.Buffer, this.WriteIndex, data.Length);
            this.WriteIndex += data.Length;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // namespace DniListener

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////