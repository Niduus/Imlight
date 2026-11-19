/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Imlight.CoreLib.Game;

/// <summary>
/// Builds a published bot update (the repository's bots folder) on the running server, against the server's own parts, so a
/// bot update needs no .NET SDK and no new Ahziael. A bot update that does not fit this server (it needs something newer)
/// simply fails to build, and the bots keep running as they are.
/// </summary>
internal static class BotBuilder {

    /// <summary>The bots' assembly name: the server's parts let it see their inner workings (AgentScriptInternals.cs).</summary>
    internal const string ASSEMBLY = "Imlight.Bots";

    /// <summary>The C# version the bots are written in (bots/Imlight.Bots.csproj LangVersion): the same here.</summary>
    private static readonly LanguageVersion s_language = LanguageVersion.CSharp13;

    /// <summary>
    /// A key for the server parts the bots are built against: a bots build made for other parts (the server was updated
    /// since) is built again before it is used.
    /// </summary>
    internal static string ServerKey() {
        var parts = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                     .Where(f => Path.GetFileName(f) is { } n && (n.StartsWith("Imlight.", StringComparison.OrdinalIgnoreCase)
                                                                 || n.StartsWith("Imcodec.", StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            if (Mvid(file) is { } mvid) {
                parts.Append(Path.GetFileName(file)).Append('=').Append(mvid).Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())))[..16];
    }

    private static Guid? Mvid(string file) {
        try {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) {
                return null;
            }

            var reader = pe.GetMetadataReader();

            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Builds the bots from <paramref name="sourceFolder"/> into <paramref name="dllPath"/> (with its .pdb). Returns null when
    /// it built, else the first errors in plain words.
    /// </summary>
    internal static string Build(string sourceFolder, string dllPath) {
        var files = Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) {
            return "the bot update has no code in it";
        }

        var options = new CSharpParseOptions(s_language);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f, Encoding.UTF8)).ToList();
        var compilation = CSharpCompilation.Create(ASSEMBLY, trees, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Disable));

        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        using (var dll = new MemoryStream())
        using (var pdb = new MemoryStream()) {
            var result = compilation.Emit(dll, pdb);
            if (!result.Success) {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)
                    .Select(d => $"{Path.GetFileName(d.Location.SourceTree?.FilePath ?? "")} line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}");

                return "it does not build on this server: " + string.Join(" | ", errors);
            }

            File.WriteAllBytes(dllPath + ".part", dll.ToArray());
            File.WriteAllBytes(pdbPath, pdb.ToArray());
            File.Move(dllPath + ".part", dllPath, overwrite: true);
        }

        return null;
    }

    // Everything the server itself runs on: .NET's own parts and the server's (Imlight, Imcodec, Akka, ...).
    private static List<MetadataReference> References() {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted) {
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
                paths.Add(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")) {
            paths.Add(path);
        }

        var references = new List<MetadataReference>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) {
            var name = Path.GetFileNameWithoutExtension(path);
            // Never the bots themselves, and each part once.
            if (string.Equals(name, ASSEMBLY, StringComparison.OrdinalIgnoreCase) || !names.Add(name) || Mvid(path) is null) {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }

}
