////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
//
// Notes:
// Setup a logging session looking for specific events.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DotnetInsights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventListener
{
    public static void Main()
    {
        HttpClient client = new HttpClient();
        var listener = new EventPipeBasedListener(data => {
            try
            {
                HttpContent content = new StringContent(data, Encoding.UTF8, "application/json");
                HttpResponseMessage response = client.PostAsync("http://localhost:2143", content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    ProcessBasedListener.Session.Dispose();
                }
            }
            catch (Exception e)
            {
                ProcessBasedListener.Session.Dispose();
            }
        });

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
                    ProcessBasedListener.Session.Dispose();
                    break;
                }
            }
            catch (Exception e)
            {
                ProcessBasedListener.Session.Dispose();
                break;
            }

            Thread.Sleep(100);
        }
    }
}
