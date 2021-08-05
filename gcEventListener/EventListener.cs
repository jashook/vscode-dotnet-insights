////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
//
// Notes:
// Setup a logging session looking for specific events.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DotnetInsights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventListener
{
    private static string[] Paths { get; set; }
    private static string LocalHostPath { get; set; }
    private static HttpClient Client { get; set; }

    private static void PostEventData(EventType type, string data)
    {
        Span<char> workingPath = stackalloc char[256];
        LocalHostPath.AsSpan().CopyTo(workingPath);

        int endPathIndex = LocalHostPath.Length;
        Span<char> endSpan = workingPath.Slice(endPathIndex);
        
        try
        {
            string endPath = "";
            if (type == EventType.GcAlloc)
            {
                endPath = Paths[0];
            }
            else if (type == EventType.GcCollection)
            {
                endPath = Paths[1];
            }
            else
            {
                Debug.Assert(type == EventType.JitEvent);
                endPath = Paths[2];
            }

            endPath.AsSpan().CopyTo(endSpan);

            Span<char> exactPath = workingPath.Slice(0, LocalHostPath.Length + endPath.Length);
            string path = exactPath.ToString();

            HttpContent content = new StringContent(data, Encoding.UTF8, "application/json");
            HttpResponseMessage response = Client.PostAsync(path, content).Result;

            if (!response.IsSuccessStatusCode)
            {
                Environment.Exit(0);
            }
        }
        catch (Exception)
        {
            Environment.Exit(0);
        }
    }

    public static void Main()
    {
        Paths = new string[] { "gcAllocation", "gcCollection", "jitEvent" };
        LocalHostPath = "http://localhost:2143/";
        Client = new HttpClient();

        var listener = new EventPipeBasedListener(listenForGcData: true, listenForAllocations: true, listenForJitEvents: true, PostEventData);

        var thread = new Thread(PingServer);
        thread.Start();

        listener.Listen();
    }

    public static void PingServer()
    {
        HttpClient client = new HttpClient();

        while (true)
        {
            // Continue to ping the server.

            try
            {
                HttpResponseMessage response = client.GetAsync("http://localhost:2143").Result;

                if (!response.IsSuccessStatusCode)
                {
                    Environment.Exit(0);
                    break;
                }
            }
            catch (Exception)
            {
                Environment.Exit(0);
                break;
            }

            Thread.Sleep(100);
        }
    }
}
