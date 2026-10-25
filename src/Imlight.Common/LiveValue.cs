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

namespace Imlight.Common;

/// <summary>
/// A value made from Imlight.ini settings that is made again after the file changes, for values too costly to make on
/// every read (sets, lists). Plain numbers and switches read <see cref="ConfigurationManager"/> directly instead.
/// </summary>
public sealed class LiveValue<T>(Func<T> make) {

    private sealed record Made(int Version, T Value);

    private volatile Made? _made;

    public T Value {
        get {
            var made = _made;
            var version = ConfigurationManager.Version;
            if (made is null || made.Version != version) {
                made = new Made(version, make());
                _made = made;
            }

            return made.Value;
        }
    }

}
