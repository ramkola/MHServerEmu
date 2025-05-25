using MHServerEmu.DatabaseAccess.Models;
using System.Buffers.Text;
using System.Diagnostics;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Powers;
using MHServerEmu.Frontend;
using System;
using System.Collections.Generic; // Needed for List
using System.Linq; // Needed for ToList
using static MHServerEmu.Games.Entities.Inventories.Inventory;
using System.Text;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("tuning")]
    [CommandGroupDescription("Command for temp tuning summons.")]
    public class TuningCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger(); // Optional: for logging

        [Command("setsummonmultiplier")]
        [CommandDescription("sets how many summons can be created")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)] // User provides 1 argument (the multiplier value)
        // CORRECTED SIGNATURE:
        public static void SetSummonMultiplier(string[] args, NetClient client)
        {
            if (args.Length < 1)
            {
                CommandHelper.SendMessage(client, "Usage: /tuning setsummonmultiplier <number>");
                return;
            }

            if (float.TryParse(args[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float multiplier))
            {
                // Ensure TunableGameplayValues class and its static property are defined and accessible
                // e.g., in MHServerEmu.Games.TunableGameplayValues
                TunableGameplayValues.SummonCountMultiplier = multiplier;
                CommandHelper.SendMessage(client, $"Summon count multiplier set to: {multiplier}");
            }
            else
            {
                CommandHelper.SendMessage(client, $"Invalid number: '{args[0]}'. Please provide a valid float value.");
            }
        }
    }
}