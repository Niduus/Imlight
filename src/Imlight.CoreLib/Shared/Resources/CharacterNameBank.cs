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
 * ========================================================================
 * WIZARD NAME BANK
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the generation and retrieval of character names from 
 * localized XML name tables, supporting complex name construction 
 * with first, middle, and last name components as they are defined
 * in the Root.wad.
 * 
 * USAGE EXAMPLE:
 * // Get a character name based on name indices and gender
 * string characterName = WizardNameBank.GetEnglishName(nameIndices, eGender.Male);
 * 
 * NOTE:
 * Pet names use the plain "FirstName" and "LastName" tables (locale section PetNames); pets have
 * no middle name table.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

public class WizardNameBank : RootSingleResourceSingleton<WizardNameBank>, IMemoryStreamDisposable {

    protected override string ResourceName => "CharacterNames.xml";
    private const string CharacterLocaleTable = "CharacterNames";
    private const string FirstNameHumanMaleTableName = "FirstName_HumanMale";
    private const string FirstNameHumanFemaleTableName = "FirstName_HumanFemale";
    private const string MiddleNameHumanTableName = "MiddleName_Human";
    private const string LastNameHumanTableName = "LastName_Human";
    private const string PetLocaleTable = "PetNames";
    private const string PetFirstNameTableName = "FirstName";
    private const string PetLastNameTableName = "LastName";
    private const string GuildLocaleTable = "AdventurePartyNames";
    private const string FirstNameGuildTableName = "FirstName_AdventureParty";
    private const string MiddleNameGuildTableName = "MiddleName_AdventureParty";
    private const string LastNameGuildTableName = "LastName_AdventureParty";

    private static Dictionary<string, List<string>> s_characterNameTable;

    protected override void AfterLoad() {
        s_characterNameTable = GetCharacterNameTable(Stream);
        Logger.Information("Loaded {0} character name tables.", Logger.Args(s_characterNameTable.Count));
    }

    /// <summary>
    /// Retrieves the English name based on the given name indices and gender.
    /// </summary>
    /// <param name="nameIndices">The name indices containing the first name, middle name, and last name.</param>
    /// <param name="gender">The gender of the character.</param>
    /// <returns>The English name composed of the first name, middle name, and last name.</returns>
    public static string GetEnglishName(uint nameIndices, eGender gender) {
        // The first 8 bits are the first name, the next 8 bits are the middle name, and the last 8 bits are the last name.
        var firstNameIndex = (int) (nameIndices >> 16) & 0xFF;
        var middleNameIndex = (int) ((nameIndices >> 8) & 0xFF);
        var lastNameIndex = (int) (nameIndices & 0xFF);

        var firstNameTableName = (gender == eGender.Male) ? FirstNameHumanMaleTableName : FirstNameHumanFemaleTableName;
        var firstName = GetEnglishNamePart(firstNameTableName, firstNameIndex);

        var middleName = string.Empty;
        if (middleNameIndex != 0) {
            middleName = GetEnglishNamePart(MiddleNameHumanTableName, middleNameIndex);
        }

        var lastName = string.Empty;
        if (lastNameIndex != 0) {
            lastName = GetEnglishNamePart(LastNameHumanTableName, lastNameIndex);
        }

        if (middleNameIndex == 0 && lastNameIndex == 0) {
            // If the middle name and last name are both 0, then the first name is the full name.
            return firstName;
        }

        return $"{firstName} {middleName}{lastName}";
    }

    /// <summary>
    /// Splits packed name keys into their first, middle and last name table indices.
    /// </summary>
    /// <param name="nameKeys">The packed name keys, as in m_nameKeys.</param>
    /// <returns>The three table indices. The top byte is not a name part and is left out.</returns>
    public static (int first, int middle, int last) GetNameParts(uint nameKeys)
        => ((int) (nameKeys >> 16) & 0xFF, (int) (nameKeys >> 8) & 0xFF, (int) nameKeys & 0xFF);

    /// <summary>
    /// Checks that packed pet name keys point into the pet name tables.
    /// </summary>
    /// <param name="nameKeys">The packed name keys a client picked for a pet.</param>
    /// <returns>True if the first and last parts are in their tables and there is no middle part.</returns>
    public static bool IsValidPetName(uint nameKeys) {
        var (first, middle, last) = GetNameParts(nameKeys);

        return middle == 0
            && first < GetTableSize(PetFirstNameTableName)
            && last < GetTableSize(PetLastNameTableName);
    }

    /// <summary>
    /// Picks a random pet name, as packed name keys: a first and a last name from the pet name tables.
    /// </summary>
    /// <param name="random">The random source.</param>
    /// <returns>The packed name keys, or 0 if the tables are not loaded.</returns>
    public static uint RandomPetName(Random random) {
        var firsts = Math.Min(GetTableSize(PetFirstNameTableName), 256);
        var lasts = Math.Min(GetTableSize(PetLastNameTableName), 256);
        if (firsts == 0 || lasts == 0) {
            return 0;
        }

        return (uint) (random.Next(firsts) << 16 | random.Next(lasts));
    }

    /// <summary>
    /// Retrieves the English name of a pet from its packed name keys.
    /// </summary>
    /// <param name="nameKeys">The packed name keys of the pet.</param>
    /// <returns>The first and last name joined by a space; some first names are empty.</returns>
    public static string GetPetEnglishName(uint nameKeys) {
        var (first, _, last) = GetNameParts(nameKeys);
        var firstName = GetEnglishNamePart(PetFirstNameTableName, first, PetLocaleTable);
        var lastName = GetEnglishNamePart(PetLastNameTableName, last, PetLocaleTable);

        return string.IsNullOrEmpty(firstName) ? lastName : $"{firstName} {lastName}";
    }

    /// <summary>
    /// Finds the name keys of a wizard name written out, such as "Walter Blanco" or "Mycin LotusKey": the first name
    /// from the gender's list, then a middle and last name written together. Case and spaces do not matter.
    /// </summary>
    /// <param name="name">The name as the game or a player wrote it.</param>
    /// <param name="gender">The wizard's gender, which picks the first name list.</param>
    /// <param name="nameKeys">The packed name keys (first, middle and last index), without the top byte.</param>
    /// <returns>True if every part of the name is in the game's name lists.</returns>
    public static bool TryFindNameKeys(string name, eGender gender, out uint nameKeys) {
        nameKeys = 0;
        if (s_characterNameTable is null || string.IsNullOrWhiteSpace(name)) {
            return false;
        }

        var words = name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstTable = (gender == eGender.Male) ? FirstNameHumanMaleTableName : FirstNameHumanFemaleTableName;
        var first = FindPart(firstTable, words[0]);
        if (first < 0) {
            return false;
        }

        if (words.Length == 1) {
            nameKeys = (uint) first << 16;

            return true;
        }

        var rest = words[1].Replace(" ", "");
        var middles = Math.Min(GetTableSize(MiddleNameHumanTableName), 256);
        var lasts = Math.Min(GetTableSize(LastNameHumanTableName), 256);
        for (var middle = 0; middle < middles; middle++) {
            var middleName = middle == 0 ? "" : GetEnglishNamePart(MiddleNameHumanTableName, middle);
            if (!rest.StartsWith(middleName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var lastPart = rest[middleName.Length..];
            for (var last = 0; last < lasts; last++) {
                var lastName = last == 0 ? "" : GetEnglishNamePart(LastNameHumanTableName, last);
                if ((middle != 0 || last != 0) && lastPart.Equals(lastName, StringComparison.OrdinalIgnoreCase)) {
                    nameKeys = (uint) (first << 16 | middle << 8 | last);

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks that packed wizard name keys point into the name lists of the gender.
    /// </summary>
    public static bool IsValidWizardName(uint nameKeys, eGender gender) {
        var (first, middle, last) = GetNameParts(nameKeys);
        var firstTable = (gender == eGender.Male) ? FirstNameHumanMaleTableName : FirstNameHumanFemaleTableName;

        return first < GetTableSize(firstTable)
            && middle < GetTableSize(MiddleNameHumanTableName)
            && last < GetTableSize(LastNameHumanTableName);
    }

    /// <summary>
    /// The English name of a guild (an adventure party name: first, middle and last part, such as "Valiant Crimson
    /// Dragons"), from packed keys in the same layout as a wizard's.
    /// </summary>
    public static string GetGuildEnglishName(uint nameKeys) {
        var (first, middle, last) = GetNameParts(nameKeys);
        var parts = new[] {
            first == 0 ? "" : GetEnglishNamePart(FirstNameGuildTableName, first, GuildLocaleTable),
            middle == 0 ? "" : GetEnglishNamePart(MiddleNameGuildTableName, middle, GuildLocaleTable),
            GetEnglishNamePart(LastNameGuildTableName, last, GuildLocaleTable),
        };

        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    /// <summary>
    /// Finds the packed keys of a guild name written out, such as "Valiant Crimson Dragons" or "Heroic Wolves": an
    /// optional first word, an optional colour and the last word from the game's adventure party name lists.
    /// </summary>
    public static bool TryFindGuildNameKeys(string name, out uint nameKeys) {
        nameKeys = 0;
        if (s_characterNameTable is null || string.IsNullOrWhiteSpace(name)) {
            return false;
        }

        var words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3) {
            return false;
        }

        var last = FindPart(LastNameGuildTableName, words[^1], GuildLocaleTable);
        if (last < 0) {
            return false;
        }

        int first = 0, middle = 0;
        if (words.Length == 3) {
            first = FindPart(FirstNameGuildTableName, words[0], GuildLocaleTable);
            middle = FindPart(MiddleNameGuildTableName, words[1], GuildLocaleTable);
        } else if (words.Length == 2) {
            first = FindPart(FirstNameGuildTableName, words[0], GuildLocaleTable);
            if (first < 0) {
                first = 0;
                middle = FindPart(MiddleNameGuildTableName, words[0], GuildLocaleTable);
            }
        }

        if (first < 0 || middle < 0) {
            return false;
        }

        nameKeys = (uint) (first << 16 | middle << 8 | last);

        return true;
    }

    /// <summary>Picks a random guild name with all three parts, as packed keys; 0 if the tables are not loaded.</summary>
    public static uint RandomGuildName(Random random) {
        var firsts = Math.Min(GetTableSize(FirstNameGuildTableName), 256);
        var middles = Math.Min(GetTableSize(MiddleNameGuildTableName), 256);
        var lasts = Math.Min(GetTableSize(LastNameGuildTableName), 256);
        if (firsts < 2 || middles < 2 || lasts == 0) {
            return 0;
        }

        return (uint) (random.Next(1, firsts) << 16 | random.Next(1, middles) << 8 | random.Next(lasts));
    }

    /// <summary>The words a guild name can be made of: first words, colours and last words.</summary>
    public static (List<string> firsts, List<string> middles, List<string> lasts) GuildNameWords() {
        List<string> Words(string table) {
            var size = Math.Min(GetTableSize(table), 256);
            var words = new List<string>();
            for (var i = 0; i < size; i++) {
                var word = GetEnglishNamePart(table, i, GuildLocaleTable);
                if (word.Length > 0) {
                    words.Add(word);
                }
            }

            return words;
        }

        return (Words(FirstNameGuildTableName), Words(MiddleNameGuildTableName), Words(LastNameGuildTableName));
    }

    private static int FindPart(string tableName, string word, string localeTable) {
        var size = Math.Min(GetTableSize(tableName), 256);
        for (var i = 0; i < size; i++) {
            var part = GetEnglishNamePart(tableName, i, localeTable);
            if (part.Length > 0 && part.Equals(word, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }

        return -1;
    }

    private static int FindPart(string tableName, string word) {
        var size = Math.Min(GetTableSize(tableName), 256);
        for (var i = 0; i < size; i++) {
            if (GetEnglishNamePart(tableName, i).Equals(word, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }

        return -1;
    }

    private static int GetTableSize(string tableName)
        => s_characterNameTable.TryGetValue(tableName, out var names) ? names.Count : 0;

    private static string GetEnglishNamePart(string tableName, int index, string localeTable = CharacterLocaleTable) {
        if (!s_characterNameTable.TryGetValue(tableName, out var characterNames)) {
            return "[TABLE_NOT_FOUND]";
        }

        if (index >= characterNames.Count) {
            return "[INDEX_OUT_OF_RANGE]";
        }

        // The character name table is just a list of locale IDs.
        var localeNameid = characterNames[index];
        var englishName = Locale.GetEnglishName(localeTable, localeNameid) ?? $"[NAME_NOT_FOUND]";

        return englishName;
    }

    private static Dictionary<string, List<string>> GetCharacterNameTable(MemoryStream fileStream) {
        // Convert the file stream to an XML document.
        var xmlDocument = new XmlDocument();
        xmlDocument.Load(fileStream);

        var result = new Dictionary<string, List<string>>();
        var tableNodes = xmlDocument.SelectNodes("/CharacterNameTable/Table");
        foreach (XmlNode tableNode in tableNodes) {
            // As of r766693 (11/15/2024), the game client now includes multiple character name tables for different locales.
            // Note that all recorded tables still reference the same, English lookup name.
            // In this case, we're only interested in the German locale since it's the first table.
            var localeAttr = tableNode.Attributes["Locale"];
            if (localeAttr != null && localeAttr.Value != "de") {
                continue;
            }

            string tableName = tableNode.Attributes["Name"].Value;
            List<string> characterNames = new List<string>();

            XmlNodeList characterNameNodes = tableNode.SelectNodes("CharacterName");
            foreach (XmlNode characterNameNode in characterNameNodes) {
                characterNames.Add(characterNameNode.InnerText);
            }

            result.Add(tableName, characterNames);
        }

        return result;
    }

    public void DisposeStream() 
        => Stream.Dispose();

}
