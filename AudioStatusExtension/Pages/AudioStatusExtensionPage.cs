// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal sealed partial class AudioStatusExtensionPage : ListPage
{
    private readonly Action _onChanged;

    public AudioStatusExtensionPage(Action onChanged)
    {
        _onChanged = onChanged;
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        Title = "Audio Status";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        return AudioStatusItems.Create(_onChanged);
    }

    public void Refresh()
    {
        RaiseItemsChanged();
    }
}
