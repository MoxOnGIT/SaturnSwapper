#define PARSE_ONLY
#undef PARSE_ONLY

using Radon.CodeAnalysis;
using Radon.CodeAnalysis.Syntax;
using Radon.CodeAnalysis.Text;
using Radon.Common;

namespace Radon.Repl;

public static class Program
{
    public static void Main()
    {
        while (true)
        {
            Console.WriteLine("Enter the path to your source file:");
            var text = Console.ReadLine();
            if (text == null)
            {
                break;
            }
            
            // If the text has quotes, then we need to remove them.
            if (text.StartsWith('"') && text.EndsWith('"'))
            {
                text = text[1..^1];
            }
            
            text = text.Trim();
            if (!File.Exists(text))
            {
                Console.WriteLine($"File '{text}' does not exist.");
                continue;
            }
            
            Log("Reading source file...", ConsoleColor.Cyan);
            var sourceText = SourceText.From(File.ReadAllText(text), text);
            Log("Parsing source file...", ConsoleColor.Cyan);
            var syntaxTree = SyntaxTree.Parse(sourceText);
            Log("Generating compilation...", ConsoleColor.Cyan);

#if PARSE_ONLY
            var parseOnlyRoot = syntaxTree.Root;
            parseOnlyRoot.WriteTo(Console.Out);
                
            var parseOnlyIncluded = syntaxTree.Included;
            foreach (var include in parseOnlyIncluded)
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine();
                include.Root.WriteTo(Console.Out);
            }
            
            continue;
#endif
            var compilation = new Compilation(syntaxTree);
            var diagnostics = compilation.Diagnostics;
            if (diagnostics.Any())
            {
                Log("Diagnostics were found!", ConsoleColor.Red);
                Console.WriteLine();
                Console.Out.WriteDiagnostics(diagnostics);
            }
            else
            {
                Log("No diagnostics were found!", ConsoleColor.Green);
#if DEBUG
                var root = syntaxTree.Root;
                root.WriteTo(Console.Out);
                
                var included = syntaxTree.Included;
                foreach (var include in included)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine();
                    include.Root.WriteTo(Console.Out);
                }
#endif

                Log("Compiling...", ConsoleColor.Cyan);
                var bytes = compilation.Compile(out diagnostics);
                if (bytes == null)
                {
                    Log("Compilation failed!", ConsoleColor.Red);
                    Console.Out.WriteDiagnostics(diagnostics);
                    continue;
                }
                
                Log("Compilation succeeded!", ConsoleColor.Green);
                Log($"Writing to {sourceText.FileName}.csp...", ConsoleColor.Cyan);
                File.WriteAllBytes(sourceText.FileName + ".csp", bytes);
                Log("Done!", ConsoleColor.Green);
            }
        }
    }

    public static void Log(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
