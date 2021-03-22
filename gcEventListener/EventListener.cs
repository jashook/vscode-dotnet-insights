////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
//
// Notes:
// Setup a logging session looking for specific events.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Net.Http;
using System.Text;
using System.Net.WebSockets;

using DotnetInsights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventListener
{
    public static void Main()
    {
        var listener = new ProcessBasedListener();

        HttpClient client = new HttpClient();
        listener.Listen(data => {
            try
            {
                HttpContent content = new StringContent(data, Encoding.UTF8, "application/json");
                var response = client.PostAsync("http://localhost:2143", content).Result;
            }
            catch (Exception e)
            {
                ProcessBasedListener.Session.Dispose();
            }
        });
    }
}
