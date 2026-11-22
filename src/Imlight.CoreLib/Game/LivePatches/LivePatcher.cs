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
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Imlight.CoreLib.Game.LivePatches;

/// <summary>
/// Puts fixed code into the running server without a restart (Niduus, 2026-11-20: "have Imlight hotload changes/fixes from
/// the repo instead of rebuilding each time ... so we can put out patches more quietly instead of SERVER RESTART").
/// <para>
/// It is .NET's own hot reload, the one Visual Studio and dotnet watch use: Roslyn compares the code this server part was
/// built from (live-patches\baseline, kept by a "Live patches" build) with the newer code, works out only what changed, and
/// .NET swaps those pieces in. A change .NET cannot make while running (deleting something, changing what a method takes,
/// ...) is refused before anything is touched, and waits for the next full update.
/// </para>
/// <para>
/// Each change that went in is also kept as a small file (live-patches\applied): the bots' mini servers are this program
/// run again, and they take the same changes from there without working anything out themselves.
/// </para>
/// </summary>
public static class LivePatcher {

    private const string CHAIN_FILE = "chain.json";

    /// <summary>The server's live patches folder.</summary>
    internal static string Folder => Path.Combine(AppContext.BaseDirectory, "live-patches");

    private static string BaselineFolder => Path.Combine(Folder, "baseline");

    private static string AppliedFolder => Path.Combine(Folder, "applied");

    /// <summary>The server part live patches change.</summary>
    internal static Assembly Target => typeof(LivePatcher).Assembly;

    private static Guid TargetId => Target.ManifestModule.ModuleVersionId;

    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static AdhocWorkspace s_workspace;
    private static object s_service;
    private static Type s_serviceType;
    private static ProjectId s_project;
    private static string s_projectDir;
    private static Solution s_current;

    // The code files that come from the repository (not the build's own generated ones), by their path in the project.
    private static Dictionary<string, DocumentId> s_docs;

    /// <summary>Whether this server was built with the "Live patches" switch on.</summary>
    internal static bool BuiltForPatches => Target.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true }
                                            && File.Exists(Path.Combine(BaselineFolder, "manifest.txt"));

    /// <summary>Whether live patches can go into this server (null) or, in plain words, why not.</summary>
    internal static string WhyNot =>
        !BuiltForPatches ? "this server was built without Live patches (turn on Live patches at the top of Ahziael; the server is built once more for it)"
        : !MetadataUpdater.IsSupported ? "the server was started without live patches ready (start it from Ahziael)"
        : null;

    /// <summary>What happened with a live patch.</summary>
    internal sealed record Outcome(bool Done, int Methods, string Problem, IReadOnlyList<string> Notes, bool NothingNew = false);

    /// <summary>
    /// Brings the running server part to the code in <paramref name="repositoryRoot"/> (a copy of the repository holding
    /// src\Imlight.CoreLib). Only what differs from what runs now is changed.
    /// </summary>
    internal static async Task<Outcome> ApplyAsync(string repositoryRoot, string label, CancellationToken cancel = default) {
        await s_lock.WaitAsync(cancel);
        try {
            if (WhyNot is { } why) {
                return new Outcome(false, 0, why, []);
            }

            if (s_workspace is null && await OpenAsync(cancel) is { } failed) {
                return new Outcome(false, 0, failed, []);
            }

            var newProject = Path.Combine(repositoryRoot, "src", "Imlight.CoreLib");
            if (!Directory.Exists(newProject)) {
                return new Outcome(false, 0, "the patch holds no server code", []);
            }

            var next = s_current;
            var docs = new Dictionary<string, DocumentId>(s_docs, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, id) in s_docs) {
                var file = Path.GetFullPath(Path.Combine(newProject, rel));
                seen.Add(file);
                if (!File.Exists(file)) {
                    next = next.RemoveDocument(id);
                    docs.Remove(rel);

                    continue;
                }

                var text = await File.ReadAllTextAsync(file, cancel);
                var old = (await next.GetDocument(id)!.GetTextAsync(cancel)).ToString();
                if (!Same(old, text)) {
                    next = next.WithDocumentText(id, SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256));
                }
            }

            foreach (var file in Directory.EnumerateFiles(newProject, "*.cs", SearchOption.AllDirectories)) {
                var full = Path.GetFullPath(file);
                var rel = Path.GetRelativePath(newProject, full);
                if (seen.Contains(full) || IsBuildOutput(rel)) {
                    continue;
                }

                var id = DocumentId.CreateNewId(s_project);
                next = next.AddDocument(id, Path.GetFileName(full),
                    SourceText.From(await File.ReadAllTextAsync(full, cancel), Encoding.UTF8, SourceHashAlgorithm.Sha256),
                    filePath: Path.Combine(s_projectDir, rel));
                docs[rel] = id;
            }

            if (next == s_current) {
                return new Outcome(true, 0, null, [], NothingNew: true);
            }

            // A fix that needs other server parts that are newer than this server's does not build here.
            var compilation = await next.GetProject(s_project)!.GetCompilationAsync(cancel);
            var errors = compilation!.GetDiagnostics(cancel).Where(d => d.Severity == DiagnosticSeverity.Error).Take(4).ToList();
            if (errors.Count > 0) {
                return new Outcome(false, 0, "it needs a newer server than this one (it does not build here: " +
                    string.Join(" | ", errors.Select(Plain)) + ")", []);
            }

            // A field or property the change adds to a class that already runs, with a starting value: the objects made before
            // it went in get the field with no value (null, 0), never the starting one, and code reading it breaks there. v0.10.941
            // added two lists to the zones' trigger part; every zone open at the time threw on each arrival and doors stopped
            // taking anyone on for 15 minutes (2026-11-21 00:31-00:46). Such a change waits for a restart instead.
            var before = await s_current.GetProject(s_project)!.GetCompilationAsync(cancel);
            if (before is not null && NewStartValues(before, compilation) is { Count: > 0 } started) {
                return new Outcome(false, 0, "it adds a starting value the server's running parts would not get (" +
                    string.Join(", ", started.Take(4)) + (started.Count > 4 ? ", ..." : "") + "), so it needs a restart", []);
            }

            // A new answer to a game message: each session's list of which part answers what is made once, when the server
            // starts, so a handler that comes in by a live patch is never called. v0.10.960 added the Friendly Players quest
            // answer that way; the game kept getting "No Quest" (2026-11-21 20:39, "does not handle yet"). It waits for a restart.
            if (before is not null && NewMessageHandlers(before, compilation) is { Count: > 0 } handlers) {
                return new Outcome(false, 0, "it adds an answer to a game message that only a restart switches on (" +
                    string.Join(", ", handlers.Take(4)) + (handlers.Count > 4 ? ", ..." : "") + "), so it needs a restart", []);
            }

            var steps = await GoInAsync(s_current, next, label, 0, cancel);
            if (steps.Problem is not null) {
                if (steps.Reached != s_current) {
                    // Part of it went in: what runs now is that part.
                    s_current = steps.Reached;
                    s_docs = DocsOf(steps.Reached);
                }

                return new Outcome(false, 0, steps.Problem, []);
            }

            s_current = next;
            s_docs = docs;

            return new Outcome(true, steps.Methods, null, steps.Notes.Distinct().ToList());
        }
        catch (Exception e) {
            Logger.Error("Live patches: {0} could not go in: {1}", Logger.Args(label, e));

            return new Outcome(false, 0, $"it failed while going in ({e.Message})", []);
        }
        finally {
            s_lock.Release();
        }
    }

    /// <summary>
    /// Fields and auto-properties the new code adds, with a starting value, to classes that the old code has too ("Type.name").
    /// A live patch gives objects already made such a member without its starting value.
    /// </summary>
    internal static List<string> NewStartValues(Compilation before, Compilation after) {
        var found = new List<string>();
        var oldTypes = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var type in TypesOf(before.Assembly.GlobalNamespace)) {
            oldTypes.TryAdd(type.ToDisplayString(), type);
        }

        foreach (var type in TypesOf(after.Assembly.GlobalNamespace)) {
            if (!oldTypes.TryGetValue(type.ToDisplayString(), out var old)) {
                continue;
            }

            var had = new HashSet<string>(old.GetMembers().Select(m => m.Name), StringComparer.Ordinal);
            foreach (var member in type.GetMembers().Where(m => !had.Contains(m.Name) && !m.IsImplicitlyDeclared)) {
                var started = member switch {
                    IFieldSymbol { IsConst: false } field => field.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null }),
                    IPropertySymbol property => property.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null }),
                    _ => false,
                };
                if (started) {
                    found.Add(type.Name + "." + member.Name);
                }
            }
        }

        return found;

        static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceSymbol space) {
            foreach (var type in space.GetTypeMembers()) {
                foreach (var t in Nested(type)) {
                    yield return t;
                }
            }

            foreach (var inner in space.GetNamespaceMembers()) {
                foreach (var t in TypesOf(inner)) {
                    yield return t;
                }
            }
        }

        static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) {
            yield return type;
            foreach (var inner in type.GetTypeMembers()) {
                foreach (var t in Nested(inner)) {
                    yield return t;
                }
            }
        }
    }

    /// <summary>
    /// Methods the new code marks as answering a message ([MessageHandler] or [InternalMessageHandler], "Type.Method") that
    /// the old code did not: the services' message lists are read once at start, so these are only called after a restart.
    /// </summary>
    internal static List<string> NewMessageHandlers(Compilation before, Compilation after) {
        var oldHandlers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, method) in Handlers(before)) {
            oldHandlers.Add(type + "." + method);
        }

        return [.. Handlers(after).Select(h => h.Type + "." + h.Method).Where(h => !oldHandlers.Contains(h)).Distinct()];

        static IEnumerable<(string Type, string Method)> Handlers(Compilation compilation) {
            foreach (var tree in compilation.SyntaxTrees) {
                var model = compilation.GetSemanticModel(tree);
                foreach (var syntax in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()) {
                    if (!syntax.AttributeLists.SelectMany(l => l.Attributes).Any(IsHandlerName)) {
                        continue;
                    }

                    if (model.GetDeclaredSymbol(syntax) is IMethodSymbol method && method.GetAttributes().Any(IsHandler)) {
                        yield return (method.ContainingType.ToDisplayString(), method.Name + "(" + string.Join(",",
                            method.Parameters.Select(p => p.Type.ToDisplayString())) + ")");
                    }
                }
            }
        }

        static bool IsHandlerName(AttributeSyntax attribute)
            => attribute.Name.ToString().Contains("MessageHandler", StringComparison.Ordinal);

        static bool IsHandler(AttributeData attribute) {
            for (var type = attribute.AttributeClass; type is not null; type = type.BaseType) {
                if (type.Name == "MessageHandlerAttribute") {
                    return true;
                }
            }

            return false;
        }
    }

    private static bool Same(string a, string b) =>
        a.TrimStart('﻿').ReplaceLineEndings("\n") == b.TrimStart('﻿').ReplaceLineEndings("\n");

    private static bool IsBuildOutput(string rel) {
        var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return first is "obj" or "bin";
    }

    private static string Plain(Diagnostic d) {
        var where = d.Location.SourceTree?.FilePath is { Length: > 0 } path
            ? $"{Path.GetFileName(path)} line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: " : "";

        return where + d.GetMessage();
    }

    /// <summary>Reads the code this server part was built from, the way it was built, and starts Roslyn's hot reload on it.</summary>
    private static async Task<string> OpenAsync(CancellationToken cancel) {
        var watch = Stopwatch.StartNew();
        var lines = File.ReadAllLines(Path.Combine(BaselineFolder, "manifest.txt"))
            .Select(l => l.Trim().TrimStart('﻿'))
            .Where(l => l.Contains('|'))
            .Select(l => (Kind: l[..l.IndexOf('|')], Value: l[(l.IndexOf('|') + 1)..]))
            .ToList();
        string One(string kind) => lines.FirstOrDefault(l => l.Kind == kind).Value ?? "";

        s_projectDir = One("project");
        var language = LanguageVersionFacts.TryParse(One("lang"), out var parsed) ? parsed : LanguageVersion.Default;
        var parse = new CSharpParseOptions(language, preprocessorSymbols: One("define").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var nullable = One("nullable").ToLowerInvariant() switch {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            deterministic: true,
            nullableContextOptions: nullable,
            allowUnsafe: One("unsafe").Equals("true", StringComparison.OrdinalIgnoreCase),
            checkOverflow: One("checked").Equals("true", StringComparison.OrdinalIgnoreCase));

        // The parts it was built against: the same files when they are still on this PC, else the server's own copy.
        var references = new List<MetadataReference>();
        foreach (var path in lines.Where(l => l.Kind == "ref").Select(l => l.Value)) {
            var file = File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path));
            if (!File.Exists(file)) {
                return $"a part it was built against is gone from this PC ({Path.GetFileName(path)}); live patches wait for the next full update";
            }

            references.Add(MetadataReference.CreateFromFile(file));
        }

        var analyzers = lines.Where(l => l.Kind == "analyzer" && File.Exists(l.Value))
            .Select(l => (AnalyzerReference) new AnalyzerFileReference(l.Value, AnalyzerLoader.Instance))
            .ToList();

        var dll = Target.Location;
        if (string.IsNullOrEmpty(dll) || !File.Exists(Path.ChangeExtension(dll, ".pdb"))) {
            return "the server part's debug file is missing, so a patch can't be worked out";
        }

        s_project = ProjectId.CreateNewId();
        s_docs = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<DocumentInfo>();
        foreach (var rel in lines.Where(l => l.Kind == "src").Select(l => l.Value)) {
            var copy = Path.Combine(BaselineFolder, "src", rel.Replace("..", "__up"));
            if (!File.Exists(copy)) {
                return $"the copy of the server's code is missing {rel}";
            }

            var id = DocumentId.CreateNewId(s_project);
            // The exact bytes it was built from, so Roslyn matches them with the debug file.
            SourceText text;
            await using (var stream = File.OpenRead(copy)) {
                text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
            }

            var path = Path.GetFullPath(Path.Combine(s_projectDir, rel));
            documents.Add(DocumentInfo.Create(id, Path.GetFileName(path), filePath: path,
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path))));
            if (!IsBuildOutput(rel)) {
                s_docs[rel] = id;
            }
        }

        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies
            .Add(typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly)
            .Add(Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")));
        var workspace = new AdhocWorkspace(host);
        var name = One("assembly");
        workspace.AddProject(ProjectInfo.Create(s_project, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: Path.Combine(s_projectDir, name + ".csproj"), outputFilePath: dll,
                compilationOptions: options, parseOptions: parse, documents: documents,
                metadataReferences: references, analyzerReferences: analyzers)
            .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(dll)));

        var compilation = await workspace.CurrentSolution.GetProject(s_project)!.GetCompilationAsync(cancel);
        var errors = compilation!.GetDiagnostics(cancel).Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).ToList();
        if (errors.Count > 0) {
            workspace.Dispose();

            return "the copy of the server's code does not build again here: " + string.Join(" | ", errors.Select(Plain));
        }

        // Roslyn's hot reload service (internal: dotnet watch and Visual Studio reach it the same way). Pinned with the
        // Roslyn version above, so a newer Roslyn is checked here before it ships.
        s_serviceType = typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly
            .GetType("Microsoft.CodeAnalysis.ExternalAccess.UnitTesting.Api.UnitTestingHotReloadService", throwOnError: true)!;
        s_service = Activator.CreateInstance(s_serviceType, workspace.Services)!;
        UseServerDebugFile(s_service, dll);
        var capabilities = ((string) typeof(MetadataUpdater).GetMethod("GetCapabilities", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
        await (Task) s_serviceType.GetMethod("StartSessionAsync")!.Invoke(s_service, [workspace.CurrentSolution, capabilities, cancel])!;

        s_workspace = workspace;
        s_current = workspace.CurrentSolution;
        Logger.Information("Live patches: the server's own code was read in {0:0.0} s; patches can be worked out now.",
            Logger.Args(watch.Elapsed.TotalSeconds));

        return null;
    }

    /// <summary>
    /// Roslyn looks for the server part's debug file where the build wrote it (the path inside the dll: Ahziael's Source
    /// folder) before the copy beside the running dll, and keeps it open for as long as the server runs. Ahziael could
    /// then neither move nor delete its Source folder, so every later update stopped with "Access to the path
    /// 'Imlight.CoreLib.pdb' is denied" (2026-11-21 00:56, 0.10.942). The copy beside the running dll is the same file
    /// and is only swapped while the server is stopped, so the session is pointed at it.
    /// </summary>
    private static void UseServerDebugFile(object service, string dll) {
        try {
            var pdb = Path.ChangeExtension(dll, ".pdb");
            var features = typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly;
            var outputsType = features.GetType("Microsoft.CodeAnalysis.Emit.CompilationOutputFiles", throwOnError: true)!;
            var enc = service.GetType().GetField("_encService", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(service);
            var field = enc?.GetType().GetField("_compilationOutputsProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is null) {
                Logger.Information("Live patches: the server's debug file is read where it was built (this Roslyn has no way to point it elsewhere).");

                return;
            }

            var outputs = Activator.CreateInstance(outputsType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, [dll, pdb], null)!;
            var provider = Delegate.CreateDelegate(field.FieldType, outputs,
                typeof(LivePatcher).GetMethod(nameof(SameOutputs), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(field.FieldType.GetGenericArguments()[1]));
            field.SetValue(enc, provider);
        }
        catch (Exception ex) {
            Logger.Information("Live patches: the server's debug file is read where it was built ({0}).", Logger.Args(ex.Message));
        }
    }

    // Closed over the outputs object: every project of the one-project workspace has the same outputs.
    private static T SameOutputs<T>(T outputs, Project project) => outputs;

    private sealed record Update(ImmutableArray<byte> Metadata, ImmutableArray<byte> IL, ImmutableArray<byte> Pdb, int Methods);

    private sealed record Steps(int Methods, string Problem, List<string> Notes, Solution Reached);

    private static Dictionary<string, DocumentId> DocsOf(Solution solution) {
        var docs = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in solution.GetProject(s_project)!.Documents) {
            var rel = Path.GetRelativePath(s_projectDir, doc.FilePath ?? "");
            if (!IsBuildOutput(rel)) {
                docs[rel] = doc.Id;
            }
        }

        return docs;
    }

    /// <summary>
    /// Works out the change from <paramref name="from"/> (what runs now) to <paramref name="to"/> and puts it into the
    /// running server. Roslyn's own change finder can fail on some sets of edits made together (2026-11-21, the 0.10.936
    /// update: two methods in one file whose bodies moved into new methods threw "An item with the same key has already been
    /// added. Key: DeletedPEMethodDefinition"); such a change goes in as smaller steps, each a working server, ending at the
    /// same code.
    /// </summary>
    private static async Task<Steps> GoInAsync(Solution from, Solution to, string label, int depth, CancellationToken cancel) {
        List<Update> updates;
        List<Diagnostic> notes;
        try {
            (updates, notes) = await EmitAsync(to, cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException && depth < 10) {
            var inner = e is TargetInvocationException { InnerException: { } real } ? real : e;
            if (await SplitAsync(from, to, cancel) is not { } middle) {
                return new Steps(0, $"it failed while going in ({inner.Message})", [], from);
            }

            Logger.Information("Live patches: {0} goes in as smaller steps ({1}).", Logger.Args(label, inner.Message));
            var first = await GoInAsync(from, middle, label, depth + 1, cancel);
            if (first.Problem is not null) {
                return first;
            }

            var second = await GoInAsync(middle, to, label, depth + 1, cancel);

            return second.Problem is not null
                ? second with { Problem = second.Problem + " (part of it is already in; the rest waits for the next full update)" }
                : new Steps(first.Methods + second.Methods, null, [.. first.Notes, .. second.Notes], to);
        }

        var blockers = notes.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (blockers.Count > 0 || (updates.Count == 0 && from != to)) {
            var reason = blockers.Count > 0
                ? string.Join(" | ", blockers.Take(4).Select(Plain))
                : notes.Count > 0 ? string.Join(" | ", notes.Take(4).Select(Plain)) : "no change could be worked out";

            return new Steps(0, "it can't go into a running server: " + reason, [], from);
        }

        var methods = 0;
        foreach (var update in updates) {
            methods += update.Methods;
            MetadataUpdater.ApplyUpdate(Target, update.Metadata.AsSpan(), update.IL.AsSpan(), update.Pdb.AsSpan());
            Keep(label, update);
        }

        return new Steps(methods, null, notes.Where(d => d.Id.StartsWith("ENC", StringComparison.Ordinal)).Select(Plain).ToList(), to);
    }

    /// <summary>
    /// A code between <paramref name="from"/> and <paramref name="to"/> that builds: part of the changed lines in, the rest
    /// not yet. Null when none is found (a single change can't be split).
    /// </summary>
    private static async Task<Solution> SplitAsync(Solution from, Solution to, CancellationToken cancel) {
        var oldProject = from.GetProject(s_project)!;
        var newProject = to.GetProject(s_project)!;
        var changes = new List<(DocumentId Doc, TextChange Change)>();
        foreach (var id in newProject.DocumentIds.Where(oldProject.ContainsDocument)) {
            var before = oldProject.GetDocument(id)!;
            var after = newProject.GetDocument(id)!;
            if (await before.GetTextAsync(cancel) is var oldText && await after.GetTextAsync(cancel) is var newText && oldText.ContentEquals(newText)) {
                continue;
            }

            // Changed lines, the way a diff shows them.
            changes.AddRange((await after.GetTextChangesAsync(before, cancel)).Select(c => (id, c)));
        }

        var added = newProject.DocumentIds.Where(id => !oldProject.ContainsDocument(id)).ToList();
        if (changes.Count < 2) {
            return null;
        }

        // The middle first; else nearer one end. Files added by the change come in with the first part.
        foreach (var take in new[] { changes.Count / 2, changes.Count / 3, changes.Count * 2 / 3, 1, changes.Count - 1 }.Distinct()) {
            if (take <= 0 || take >= changes.Count) {
                continue;
            }

            var middle = from;
            foreach (var id in added) {
                var doc = newProject.GetDocument(id)!;
                middle = middle.AddDocument(DocumentInfo.Create(id, doc.Name, doc.Folders, filePath: doc.FilePath,
                    loader: TextLoader.From(TextAndVersion.Create(await doc.GetTextAsync(cancel), VersionStamp.Create(), doc.FilePath))));
            }

            foreach (var group in changes.Take(take).GroupBy(c => c.Doc)) {
                var text = await middle.GetDocument(group.Key)!.GetTextAsync(cancel);
                middle = middle.WithDocumentText(group.Key, text.WithChanges(group.Select(c => c.Change)));
            }

            var compilation = await middle.GetProject(s_project)!.GetCompilationAsync(cancel);
            if (!compilation!.GetDiagnostics(cancel).Any(d => d.Severity == DiagnosticSeverity.Error)) {
                return middle;
            }
        }

        return null;
    }

    private static async Task<(List<Update> Updates, List<Diagnostic> Notes)> EmitAsync(Solution solution, CancellationToken cancel) {
        var task = (Task) s_serviceType.GetMethod("EmitSolutionUpdateAsync")!.Invoke(s_service, [solution, true, cancel])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var updates = new List<Update>();
        foreach (var update in (IEnumerable) result.GetType().GetField("Item1")!.GetValue(result)!) {
            var type = update.GetType();
            if ((Guid) type.GetField("ModuleId")!.GetValue(update)! != TargetId) {
                continue;
            }

            updates.Add(new Update(
                (ImmutableArray<byte>) type.GetField("MetadataDelta")!.GetValue(update)!,
                (ImmutableArray<byte>) type.GetField("ILDelta")!.GetValue(update)!,
                (ImmutableArray<byte>) type.GetField("PdbDelta")!.GetValue(update)!,
                ((ImmutableArray<int>) type.GetField("UpdatedMethods")!.GetValue(update)!).Length));
        }

        var notes = ((ImmutableArray<Diagnostic>) result.GetType().GetField("Item2")!.GetValue(result)!)
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("ENC", StringComparison.Ordinal))
            .ToList();

        return (updates, notes);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The changes that went in, kept for the bots' mini servers (the same program, run again).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A name for this run of the game server: the kept changes belong to it.</summary>
    internal static readonly string Run = Guid.NewGuid().ToString("N")[..12];

    private static int s_kept;
    private static bool s_keeping;

    /// <summary>The game server starts: changes kept by an earlier run belong to that run and are cleared.</summary>
    internal static void StartChain() {
        try {
            if (Directory.Exists(AppliedFolder)) {
                Directory.Delete(AppliedFolder, recursive: true);
            }

            Directory.CreateDirectory(AppliedFolder);
            WriteChain([]);
            s_keeping = true;
        }
        catch (Exception e) {
            Logger.Warning("Live patches: could not clear the last run's changes: {0}", Logger.Args(e.Message));
        }
    }

    private static void Keep(string label, Update update) {
        // Only the game server keeps them (not the live patch check).
        if (!s_keeping) {
            return;
        }

        try {
            var step = ++s_kept;
            File.WriteAllBytes(Path.Combine(AppliedFolder, $"{step}.meta"), [.. update.Metadata]);
            File.WriteAllBytes(Path.Combine(AppliedFolder, $"{step}.il"), [.. update.IL]);
            File.WriteAllBytes(Path.Combine(AppliedFolder, $"{step}.pdb"), [.. update.Pdb]);
            var steps = ReadChain()?["steps"]?.AsArray().Select(s => s!.DeepClone()).ToList() ?? [];
            steps.Add(new JsonObject { ["step"] = step, ["label"] = label });
            WriteChain(steps);
        }
        catch (Exception e) {
            Logger.Warning("Live patches: could not keep the change for the bots' mini servers: {0}", Logger.Args(e.Message));
        }
    }

    private static void WriteChain(List<JsonNode> steps) {
        var path = Path.Combine(AppliedFolder, CHAIN_FILE);
        File.WriteAllText(path + ".tmp", new JsonObject {
            ["run"] = Run,
            ["module"] = TargetId.ToString(),
            ["steps"] = new JsonArray([.. steps]),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static JsonNode ReadChain() {
        try {
            var path = Path.Combine(AppliedFolder, CHAIN_FILE);

            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// A bots' mini server: takes the changes the game server put in, now and as more come (checked every 15 seconds).
    /// A mini server started for another run of the game server, or built from other code, takes none.
    /// </summary>
    public static void FollowGameServer() {
        if (WhyNot is not null) {
            return;
        }

        _ = Task.Run(async () => {
            string run = null;
            var taken = 0;
            while (true) {
                try {
                    var chain = ReadChain();
                    var chainRun = (string) chain?["run"] ?? "";
                    if (chain is not null && (string) chain["module"] == TargetId.ToString()) {
                        run ??= chainRun;
                        if (chainRun != run) {
                            Logger.Information("Live patches: the game server restarted; this mini server keeps the changes it has.");

                            return;
                        }

                        foreach (var step in chain["steps"]!.AsArray().Select(s => (int) s!["step"]!).Where(s => s > taken).OrderBy(s => s)) {
                            MetadataUpdater.ApplyUpdate(Target,
                                File.ReadAllBytes(Path.Combine(AppliedFolder, $"{step}.meta")),
                                File.ReadAllBytes(Path.Combine(AppliedFolder, $"{step}.il")),
                                File.ReadAllBytes(Path.Combine(AppliedFolder, $"{step}.pdb")));
                            taken = step;
                            Logger.Information("Live patches: this mini server took change {0} from the game server.", Logger.Args(step));
                        }
                    }
                }
                catch (Exception e) {
                    Logger.Error("Live patches: this mini server could not take the game server's changes and keeps what it has: {0}", Logger.Args(e));

                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        });
    }

    private sealed class AnalyzerLoader : IAnalyzerAssemblyLoader {

        internal static readonly AnalyzerLoader Instance = new();

        public void AddDependencyLocation(string fullPath) {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);

    }

}
