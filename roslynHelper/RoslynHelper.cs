namespace dotnetInsights
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                // Output file expected.
                Console.WriteLine("Error, output file expected.");
                return;
            }

            while (true)
            {
                string fileToCompile = Console.ReadLine();
                fileToCompile = fileToCompile.Trim();

                if (!File.Exists(fileToCompile))
                {
                    break;
                }

                CompilerHelper helper = new CompilerHelper(args[0]);
                List<string> failures = helper.CompileFile(fileToCompile);

                if (failures == null)
                {
                    Console.WriteLine("Compilation succeeded");
                }
                else
                {
                    foreach (string item in failures)
                    {
                        Console.WriteLine(item);
                    }
                }
            }
        }
    }
}
