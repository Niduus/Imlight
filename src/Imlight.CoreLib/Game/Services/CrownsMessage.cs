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
 */

using System;
using System.Linq;
using System.Reflection;
using Imcodec.MessageLayer;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Tells the game how many crowns a wizard has. The game's crown messages come from its own message files at
/// build time, so the message is found by name when the server starts instead of being named in code. Every
/// crown message found is logged with its fields.
/// </summary>
internal static class CrownsMessage {

    private static readonly Lazy<(Type Type, MemberInfo Amount)?> s_message = new(Find);

    internal static IMessage Create(int crowns) {
        if (s_message.Value is not { } found || Activator.CreateInstance(found.Type) is not IMessage message) {
            return null;
        }

        if (found.Amount is FieldInfo field) {
            field.SetValue(message, Convert.ChangeType(crowns, field.FieldType));
        }
        else if (found.Amount is PropertyInfo property) {
            property.SetValue(message, Convert.ChangeType(crowns, property.PropertyType));
        }

        return message;
    }

    // Messages for things still being worked out (the badge window, the crown shop, jewels and pins, the deck's Archmastery
    // school), logged with their fields so the next step can use them.
    private static readonly string[] s_loggedMessageWords = ["BADGE", "PCS", "CROWNSHOP", "CROWNSERVICE", "ELIXIR", "ARCHMASTERY", "JEWEL", "SOCKET", "PIN"];

    private static void LogOtherMessages() {
        try {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Imcodec", StringComparison.Ordinal) == true)
                .SelectMany(LoadableTypes)
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMessage).IsAssignableFrom(t)
                         && s_loggedMessageWords.Any(word => t.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
            foreach (var type in types) {
                Logger.Information("Message: the game has {0} ({1}).", Logger.Args(type.FullName,
                    string.Join(", ", type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(m => $"{m.Name} {m.FieldType.Name}")
                        .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(m => m.CanWrite)
                            .Select(m => $"{m.Name} {m.PropertyType.Name}")))));
            }
        }
        catch (Exception e) {
            Logger.Warning("Message list could not be logged: {0}", Logger.Args(e.Message));
        }
    }

    private static (Type, MemberInfo)? Find() {
        try {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Imcodec", StringComparison.Ordinal) == true)
                .SelectMany(LoadableTypes)
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMessage).IsAssignableFrom(t)
                         && t.Name.Contains("CROWN", StringComparison.OrdinalIgnoreCase))
                .ToList();

            LogOtherMessages();

            (Type Type, MemberInfo Amount, int Score)? best = null;
            foreach (var type in types) {
                var members = NumericMembers(type).ToList();
                Logger.Information("Crowns: the game has {0} ({1}).", Logger.Args(type.FullName,
                    string.Join(", ", type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(m => $"{m.Name} {m.FieldType.Name}")
                        .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(m => m.CanWrite)
                            .Select(m => $"{m.Name} {m.PropertyType.Name}")))));

                foreach (var member in members) {
                    var score = Score(type.Name, member.Name);
                    if (score > 0 && (best is null || score > best.Value.Score)) {
                        best = (type, member, score);
                    }
                }
            }

            if (best is null) {
                Logger.Warning("Crowns: no crown message with a crown amount was found, so the game is not told about crowns.");

                return null;
            }

            Logger.Information("Crowns: using {0}.{1} to tell the game the crown balance.",
                Logger.Args(best.Value.Type.Name, best.Value.Amount.Name));

            return (best.Value.Type, best.Value.Amount);
        }
        catch (Exception e) {
            Logger.Warning("Crowns: looking for the crown message failed: {0}", Logger.Args(e.Message));

            return null;
        }
    }

    private static Type[] LoadableTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e) {
            return [.. e.Types.Where(t => t is not null)];
        }
    }

    private static readonly Type[] s_numberTypes = [typeof(int), typeof(uint), typeof(long), typeof(ulong)];

    private static System.Collections.Generic.IEnumerable<MemberInfo> NumericMembers(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => s_numberTypes.Contains(f.FieldType))
            .Cast<MemberInfo>()
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && s_numberTypes.Contains(p.PropertyType)));

    // Prefers a balance update (UPDATE, BALANCE, WALLET) and an amount field named for crowns.
    private static int Score(string typeName, string memberName) {
        var member = memberName.ToLowerInvariant();
        var score = member.Contains("crown") ? 4
            : member is "amount" or "balance" or "total" or "count" or "value" ? 2
            : 0;
        if (score == 0) {
            return 0;
        }

        var type = typeName.ToUpperInvariant();
        if (type.Contains("UPDATE") || type.Contains("BALANCE") || type.Contains("WALLET")) {
            score += 3;
        }
        if (type.Contains("REQUEST") || type.Contains("BUY") || type.Contains("PURCHASE") || type.Contains("COST")) {
            score -= 3;
        }

        return score;
    }

}
