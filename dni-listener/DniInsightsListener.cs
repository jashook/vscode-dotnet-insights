////////////////////////////////////////////////////////////////////////////////
// Module: DniInsightsListener.cs
//
// Notes:
//  Command line tool for automatically capturing managed traces.
////////////////////////////////////////////////////////////////////////////////

using System;

namespace DniListener
{
    class DniInsightsListener
    {
        static int Main(string configurationFile)
        {
            if (configurationFile == string.IsNullOrEmpty)
            {
                Console.WriteLine("Configuration file is a required argument.");
                return -1;
            }
            
            if (!File.Exists(configurationFile))
            {
                Console.WriteLine("Configuration file path does not exist. Please check path.");
                return -2;
            }

            // Parse the configuration file.
            ConfigFile file = ConfigFile.Parse(configurationFile);
        }
    }
}