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

// In the generated types' namespace, so every file that reads NPCBehaviorTemplate has it without a using.
namespace Imcodec.ObjectProperty.TypeCache;

/// <summary>
/// Whether a creature is a boss. The game marks it two ways: the old m_bossMob flag, and m_mobTitle, the title the
/// game shows on its name plate. Azteca and later bosses (Tolkemec and about 800 more) have only the title, so
/// either one counts (Niduus, 2026-11-03: they were missing from the Manager's boss search).
/// </summary>
public static class BossRank {

    public static bool IsBoss(this NPCBehaviorTemplate npc)
        => npc is not null && (npc.m_bossMob || npc.m_mobTitle == TITLE_TYPE.Boss);

}
