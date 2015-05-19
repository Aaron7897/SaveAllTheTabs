﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace TabGroups
{
    internal class StashCommands
    {
        [Guid(TabGroupsPackageGuids.StashCmdSetGuidString)]
        private enum StashCommandIds
        {
            StashSaveGroup = 0x0100,
            StashRestoreGroup = 0x0200
        }

        private TabGroupsPackage Package { get; }

        public StashCommands(TabGroupsPackage package)
        {
            Package = package;
        }

        public void SetupCommands(OleMenuCommandService commandService)
        {
            var guid = typeof(StashCommandIds).GUID;

            var commandId = new CommandID(guid, (int)StashCommandIds.StashSaveGroup);
            var command = new OleMenuCommand(ExecuteStashSaveGroupCommand, commandId);
            command.BeforeQueryStatus += CommandOnBeforeQueryStatus;
            commandService.AddCommand(command);

            commandId = new CommandID(guid, (int)StashCommandIds.StashRestoreGroup);
            command = new OleMenuCommand(ExecuteStashRestoreGroupCommand, commandId);
            command.BeforeQueryStatus += StashRestoreGroupCommandOnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        private void CommandOnBeforeQueryStatus(object sender, EventArgs eventArgs)
        {
            var command = sender as OleMenuCommand;
            if (command == null)
            {
                return;
            }

            command.Enabled = Package.DocumentManager != null;
        }

        private void StashRestoreGroupCommandOnBeforeQueryStatus(object sender, EventArgs e)
        {
            var command = sender as OleMenuCommand;
            if (command == null)
            {
                return;
            }

            command.Enabled = Package.DocumentManager?.HasStashGroup == true;
        }

        private void ExecuteStashRestoreGroupCommand(object sender, EventArgs e)
        {
            Package.DocumentManager?.RestoreStashGroup();
        }

        private void ExecuteStashSaveGroupCommand(object sender, EventArgs e)
        {
            Package.DocumentManager?.SaveStashGroup();
        }
    }
}
