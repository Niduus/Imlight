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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Imlight.CoreLib.Game.LivePatches;

/// <summary>
/// A field or auto-property that a live patch adds with a starting value (Niduus, 2026-11-21: updates without "UPDATE,
/// BUILD AND RESTART"; 0.10.956 and 0.10.961 waited for a restart for this alone). Objects made before the patch would
/// get it with no value (null, 0): 0.10.941 added two lists to the zones and every open zone threw on each arrival. So
/// the running server gets it as a property that takes its starting value the first time it is read:
/// <code>
///   private readonly List&lt;int&gt; _seen = new();
/// </code>
/// goes in as
/// <code>
///   private List&lt;int&gt; __lp__seen;
///   private List&lt;int&gt; _seen { get => __lp__seen ?? Interlocked.CompareExchange(ref __lp__seen, new(), null) ?? __lp__seen; set => __lp__seen = value; }
/// </code>
/// Every later patch until the server restarts writes it the same way (the running server already has the property).
/// The code it was built from is never changed; a restart starts from that code again.
/// </summary>
internal static class LiveStartValues {

    internal const string PREFIX = "__lp_";

    /// <summary>"Namespace.Type|member" of each field or auto-property the new code adds with a starting value.</summary>
    internal static List<string> Added(Compilation before, Compilation after) {
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
                    found.Add(Key(type.ToDisplayString(), member.Name));
                }
            }
        }

        return found;
    }

    internal static string Key(string type, string member) => type + "|" + member;

    /// <summary>
    /// <paramref name="solution"/> with every member in <paramref name="keys"/> written as a property that takes its
    /// starting value when first read. Null and why when one can't be written that way.
    /// </summary>
    internal static async Task<(Solution Solution, string Problem)> MakeLazyAsync(Solution solution, ProjectId projectId, IReadOnlyCollection<string> keys,
        CancellationToken cancel) {
        if (keys.Count == 0) {
            return (solution, null);
        }

        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        var project = solution.GetProject(projectId)!;
        var compilation = await project.GetCompilationAsync(cancel);
        foreach (var document in project.Documents) {
            var root = await document.GetSyntaxRootAsync(cancel);
            if (root is null) {
                continue;
            }

            var model = compilation!.GetSemanticModel(root.SyntaxTree);
            var targets = new Dictionary<MemberDeclarationSyntax, (List<MemberDeclarationSyntax> Members, string Problem)>();
            foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>()) {
                if (declaration is not (FieldDeclarationSyntax or PropertyDeclarationSyntax)
                    || declaration.Parent is not TypeDeclarationSyntax owner
                    || model.GetDeclaredSymbol(owner, cancel) is not INamedTypeSymbol type) {
                    continue;
                }

                var typeName = type.ToDisplayString();
                switch (declaration) {
                    case FieldDeclarationSyntax field when field.Declaration.Variables.Any(v => wanted.Contains(Key(typeName, v.Identifier.ValueText))):
                        targets[field] = Field(field, typeName, wanted, model, cancel);
                        break;
                    case PropertyDeclarationSyntax property when wanted.Contains(Key(typeName, property.Identifier.ValueText)):
                        targets[property] = Property(property, model, cancel);
                        break;
                }
            }

            if (targets.Count == 0) {
                continue;
            }

            if (targets.Values.FirstOrDefault(t => t.Problem is not null).Problem is { } problem) {
                return (solution, problem);
            }

            // Each declaration becomes its members, on the declaration's own line, so every line after it keeps its number.
            var rewritten = root.TrackNodes(targets.Keys);
            foreach (var (old, (members, _)) in targets) {
                var current = rewritten.GetCurrentNode(old)!;
                var made = members.Select((m, i) => (SyntaxNode) m.NormalizeWhitespace("", " ")
                    .WithLeadingTrivia(i == 0 ? old.GetLeadingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.Space))
                    .WithTrailingTrivia(i == members.Count - 1 ? old.GetTrailingTrivia() : SyntaxFactory.TriviaList()));
                rewritten = rewritten.ReplaceNode(current, made);
            }

            solution = solution.WithDocumentSyntaxRoot(document.Id, rewritten);
        }

        return (solution, null);
    }

    private static (List<MemberDeclarationSyntax>, string) Field(FieldDeclarationSyntax field, string typeName, HashSet<string> wanted,
        SemanticModel model, CancellationToken cancel) {
        var members = new List<MemberDeclarationSyntax>();
        var isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);
        if (field.AttributeLists.Count > 0 || field.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword) || m.IsKind(SyntaxKind.FixedKeyword)
                || m.IsKind(SyntaxKind.ConstKeyword))) {
            return (members, $"{typeName}: a new field with a starting value and attributes or volatile/fixed");
        }

        var access = field.Modifiers.Where(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PrivateKeyword)
            || m.IsKind(SyntaxKind.InternalKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.NewKeyword)).ToList();
        var typeSyntax = field.Declaration.Type;
        foreach (var variable in field.Declaration.Variables) {
            var name = variable.Identifier.ValueText;
            if (!wanted.Contains(Key(typeName, name)) || variable.Initializer is null) {
                // A plain neighbour declared in the same line keeps its own declaration.
                members.Add(SyntaxFactory.FieldDeclaration(field.AttributeLists, field.Modifiers,
                    SyntaxFactory.VariableDeclaration(typeSyntax, SyntaxFactory.SingletonSeparatedList(variable))));
                continue;
            }

            if (model.GetDeclaredSymbol(variable, cancel) is not IFieldSymbol symbol) {
                return (members, $"{typeName}.{name} could not be read");
            }

            var lazy = Lazy(symbol.Type, typeSyntax, PREFIX + name, isStatic, variable.Initializer.Value, out var problem);
            if (problem is not null) {
                return (members, $"{typeName}.{name}: {problem}");
            }

            members.Add(lazy.Backing);
            var modifiers = SyntaxFactory.TokenList(access.Concat(isStatic ? [SyntaxFactory.Token(SyntaxKind.StaticKeyword)] : []));
            members.Add(SyntaxFactory.PropertyDeclaration(typeSyntax, variable.Identifier)
                .WithModifiers(modifiers)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List([lazy.Get, lazy.Set]))));
        }

        return (members, null);
    }

    private static (List<MemberDeclarationSyntax>, string) Property(PropertyDeclarationSyntax property, SemanticModel model, CancellationToken cancel) {
        var members = new List<MemberDeclarationSyntax>();
        if (property.Initializer is null || property.AccessorList is null
            || property.AccessorList.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null)
            || model.GetDeclaredSymbol(property, cancel) is not IPropertySymbol symbol) {
            return (members, $"{property.Identifier.ValueText}: a new property with a starting value that isn't a plain one");
        }

        var isStatic = property.Modifiers.Any(SyntaxKind.StaticKeyword);
        var lazy = Lazy(symbol.Type, property.Type, PREFIX + property.Identifier.ValueText, isStatic, property.Initializer.Value, out var problem);
        if (problem is not null) {
            return (members, $"{property.Identifier.ValueText}: {problem}");
        }

        members.Add(lazy.Backing);
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var accessor in property.AccessorList.Accessors) {
            var made = accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ? lazy.Get : lazy.Set.WithKeyword(accessor.Keyword).WithModifiers(accessor.Modifiers);
            accessors.Add(made.WithModifiers(accessor.Modifiers).WithAttributeLists(accessor.AttributeLists));
        }

        members.Add(property.WithInitializer(null).WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))));

        return (members, null);
    }

    private sealed record LazyParts(FieldDeclarationSyntax Backing, AccessorDeclarationSyntax Get, AccessorDeclarationSyntax Set);

    private static LazyParts Lazy(ITypeSymbol type, TypeSyntax typeSyntax, string backing, bool isStatic, ExpressionSyntax start, out string problem) {
        problem = null;
        if (type.TypeKind == TypeKind.TypeParameter || type is IPointerTypeSymbol || type.IsRefLikeType) {
            problem = "its type can't be made to start when first read";

            return null;
        }

        var nullableValue = type.IsValueType && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        // The backing field: the same type, nullable so "not set yet" can be told apart.
        var backingType = type.IsReferenceType || nullableValue
            ? typeSyntax.WithoutTrivia()
            : SyntaxFactory.NullableType(typeSyntax.WithoutTrivia());
        var modifiers = SyntaxFactory.TokenList([SyntaxFactory.Token(SyntaxKind.PrivateKeyword), .. isStatic ? new[] { SyntaxFactory.Token(SyntaxKind.StaticKeyword) } : []]);
        var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(backingType,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(backing)))).WithModifiers(modifiers);
        var startValue = start.WithoutTrivia();
        // A target-typed new() needs its type spelled out once it moves into an expression.
        if (startValue is ImplicitObjectCreationExpressionSyntax implicitNew) {
            startValue = SyntaxFactory.ObjectCreationExpression(typeSyntax.WithoutTrivia(), implicitNew.ArgumentList, implicitNew.Initializer);
        }
        else if (startValue is CollectionExpressionSyntax) {
            startValue = SyntaxFactory.CastExpression(typeSyntax.WithoutTrivia(), SyntaxFactory.ParenthesizedExpression(startValue));
        }

        // Written out as text below: spaces where the syntax made above has none ("new" and its type).
        startValue = startValue.NormalizeWhitespace("", " ");
        var name = SyntaxFactory.IdentifierName(backing);
        ExpressionSyntax read;
        if (type.IsReferenceType) {
            // Only one starting value is ever kept, even when two threads read it first at once.
            read = ParseExpression($"{backing} ?? System.Threading.Interlocked.CompareExchange(ref {backing}, ({startValue}), null) ?? {backing}");
        }
        else if (nullableValue) {
            read = ParseExpression($"{backing} ??= ({startValue})");
        }
        else {
            read = ParseExpression($"({backing} ??= ({startValue}))");
        }

        var get = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(read)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var set = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, name,
                SyntaxFactory.IdentifierName("value"))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return new LazyParts(field, get, set);
    }

    private static ExpressionSyntax ParseExpression(string text) => SyntaxFactory.ParseExpression(text);

    private static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceSymbol space) {
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

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) {
        yield return type;
        foreach (var inner in type.GetTypeMembers()) {
            foreach (var t in Nested(inner)) {
                yield return t;
            }
        }
    }

}
