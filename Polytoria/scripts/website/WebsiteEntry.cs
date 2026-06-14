// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client;
using Polytoria.Networking.P2P;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Polytoria.Website;

public sealed partial class WebsiteEntry : Node
{
    private ClientEntry? _clientEntry;

    public override void _Ready()
    {
        PT.Print("[Website] Website entry starting...");
        
        // Create ClientEntry as child (needed for WebUI to work)
        _clientEntry = new ClientEntry();
        _clientEntry.Name = "ClientEntry";
        AddChild(_clientEntry, true);
        
        // Get command line args
        Dictionary<string, string> cmdargs = Globals.ReadCmdArgs();
        
        // Force webui mode
        cmdargs["webui"] = "true";
        
        // Start ClientEntry which will initialize everything and launch WebUI
        _ = StartClientEntry(cmdargs);
    }

    private async Task StartClientEntry(Dictionary<string, string> cmdargs)
    {
        // Wait for ClientEntry to be ready
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        
        if (_clientEntry != null)
        {
            PT.Print("[Website] Starting ClientEntry with WebUI mode...");
            _clientEntry.Entry(new ClientEntry.ClientEntryData 
            {
                // No token - will run in local/P2P mode
            });
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            Globals.Singleton.Quit();
        }
        base._Notification(what);
    }
}
