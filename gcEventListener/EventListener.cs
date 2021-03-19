﻿////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
//
// Notes:
// Setup a logging session looking for specific events.
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventListener
{
    public static void Main()
    {
        var listener = new ProcessBasedListener(29388);

        listener.Listen();
    }
}