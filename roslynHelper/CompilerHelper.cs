////////////////////////////////////////////////////////////////////////////////
// Module: CompilerHelper.cs
//
// Notes:
//
// Uses Microsoft.CodeAnalysis.CSharp.Workspaces to compile the specific file
// that is passed into IL. This can then be either ILdasmed or PMIed to give
// both the human readable IL and the JITed code.
//
////////////////////////////////////////////////////////////////////////////////

namespace dotnetInsights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CompilerHelper
{
    private string OutputFile;

    // Ctor

    public CompilerHelper(string outputFile)
    {
        this.OutputFile = outputFile;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member Methods
    ////////////////////////////////////////////////////////////////////////////

    public List<string> CompileFile(string fileName)
    {
        Debug.Assert(File.Exists(fileName));

        if (!File.Exists(fileName))
        {
            return null;
        }

        string sourceCode = File.ReadAllText(fileName);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode);

        CSharpCompilationOptions options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true);

        string[] trustedAssembliesPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);

        List<MetadataReference> references = new List<MetadataReference>();
        for (int index = 0; index < trustedAssembliesPaths.Length; ++index)
        {
            references.Add(MetadataReference.CreateFromFile(trustedAssembliesPaths[index]));
        }

        Compilation compilation = CSharpCompilation.Create(assemblyName: "DynamicAssembly", options: options).AddReferences(references).AddSyntaxTrees(tree);
        
        using MemoryStream stream = new MemoryStream();
        EmitResult result = compilation.Emit(stream);

        if (result.Success)
        {
            // Do something
            stream.Position = 0;
            using (FileStream fs = File.Create(this.OutputFile))
            {
                stream.CopyTo(fs);
            }

            return null;
        }
        else
        {
            List<string> failures = new List<string>();

            foreach (var diagnostic in result.Diagnostics)
            {
                failures.Add(diagnostic.ToString());
            }

            return failures;
        }
    }
} // end of class(CompilerHelper)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(dotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////