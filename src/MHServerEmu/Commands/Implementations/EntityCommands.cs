using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Frontend;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Regions;
using MHServerEmu.Grouping;
using MHServerEmu.PlayerManagement;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("Entity")]
    [CommandGroupDescription("Entity management commands.")]
    public class EntityCommands : CommandGroup
    {
        [Command("dummy")]
        [CommandDescription("Replace the training room target dummy with the specified entity.")]
        [CommandUsage("entity dummy [pattern]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Dummy(string[] @params, NetClient client)
        {
            PrototypeId agentRef = CommandHelper.FindPrototype(HardcodedBlueprints.Agent, @params[0], client);
            if (agentRef == PrototypeId.Invalid) return string.Empty;
            var agentProto = GameDatabase.GetPrototype<AgentPrototype>(agentRef);

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            var region = player.GetRegion();
            if (region.PrototypeDataRef != (PrototypeId)12181996598405306634) // TrainingRoomSHIELDRegion
                return "Player is not in Training Room";

            bool found = false;
            Agent dummy = null;
            foreach (var entity in region.Entities)
                if (entity.PrototypeDataRef == (PrototypeId)6534964972476177451)
                {
                    found = true;
                    dummy = entity as Agent;
                }

            if (found == false) return "Dummy is not found";
            dummy.SetDormant(true);

            EntityHelper.CreateAgent(agentProto, player.CurrentAvatar, dummy.RegionLocation.Position, dummy.RegionLocation.Orientation);

            return string.Empty;
        }


        [Command("marker")]
        [CommandDescription("Displays information about the specified marker.")]
        [CommandUsage("entity marker [MarkerId]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Marker(string[] @params, NetClient client)
        {
            if (int.TryParse(@params[0], out int markerId) == false)
                return $"Failed to parse MarkerId {@params[0]}";

            PlayerConnection playerConnection = (PlayerConnection)client;

            var reservation = playerConnection.AOI.Region.SpawnMarkerRegistry.GetReservationByPid(markerId);
            if (reservation == null) return "No marker found.";

            CommandHelper.SendMessage(client, $"Marker[{markerId}]: {GameDatabase.GetFormattedPrototypeName(reservation.MarkerRef)}");
            CommandHelper.SendMessageSplit(client, reservation.ToString(), false);
            return string.Empty;
        }


        [Command("info")]
        [CommandDescription("Displays information about the specified entity.")]
        [CommandUsage("entity info [EntityId]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Info(string[] @params, NetClient client)
        {
            if (ulong.TryParse(@params[0], out ulong entityId) == false)
                return $"Failed to parse EntityId {@params[0]}";

            Game game = ((PlayerConnection)client).Game;

            var entity = game.EntityManager.GetEntity<Entity>(entityId);
            if (entity == null) return "No entity found.";

            CommandHelper.SendMessage(client, $"Entity[{entityId}]: {GameDatabase.GetFormattedPrototypeName(entity.PrototypeDataRef)}");
            CommandHelper.SendMessageSplit(client, entity.Properties.ToString(), false);
            if (entity is WorldEntity worldEntity)
            {
                CommandHelper.SendMessageSplit(client, worldEntity.Bounds.ToString(), false);
                CommandHelper.SendMessageSplit(client, worldEntity.PowerCollectionToString(), false);
                CommandHelper.SendMessageSplit(client, worldEntity.ConditionCollectionToString(), false);
            }
            return string.Empty;
        }

        [Command("near")]
        [CommandDescription("Displays all entities in a radius (default is 100).")]
        [CommandUsage("entity near [radius]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Near(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Avatar avatar = playerConnection.Player.CurrentAvatar;

            if ((@params.Length > 0 && int.TryParse(@params[0], out int radius)) == false)
                radius = 100;   // Default to 100 if no radius is specified

            Sphere near = new(avatar.RegionLocation.Position, radius);

            List<string> entities = new();
            foreach (var worldEntity in playerConnection.AOI.Region.IterateEntitiesInVolume(near, new()))
            {
                string name = worldEntity.PrototypeName;
                ulong entityId = worldEntity.Id;
                string status = string.Empty;
                if (playerConnection.AOI.InterestedInEntity(entityId) == false) status += "[H]";
                if (worldEntity is Transition) status += "[T]";
                if (worldEntity.WorldEntityPrototype.VisibleByDefault == false) status += "[Inv]";
                entities.Add($"[E][{entityId}] {name} {status}");
            }

            foreach (var reservation in playerConnection.AOI.Region.SpawnMarkerRegistry.IterateReservationsInVolume(near))
            {
                string name = GameDatabase.GetFormattedPrototypeName(reservation.MarkerRef);
                int markerId = reservation.GetPid();
                string status = $"[{reservation.Type.ToString()[0]}][{reservation.State.ToString()[0]}]";
                entities.Add($"[M][{markerId}] {name} {status}");
            }

            if (entities.Count == 0)
                return "No objects found.";

            CommandHelper.SendMessage(client, $"Found for R={radius}:");
            CommandHelper.SendMessages(client, entities, false);
            return string.Empty;
        }

        [Command("isblocked")]
        [CommandUsage("entity isblocked [EntityId1] [EntityId2]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(2)]
        public string IsBlocked(string[] @params, NetClient client)
        {
            if (ulong.TryParse(@params[0], out ulong entityId1) == false)
                return $"Failed to parse EntityId1 {@params[0]}";

            if (ulong.TryParse(@params[1], out ulong entityId2) == false)
                return $"Failed to parse EntityId2 {@params[1]}";

            Game game = ((PlayerConnection)client).Game;
            var manager = game.EntityManager;

            var entity1 = manager.GetEntity<WorldEntity>(entityId1);
            if (entity1 == null) return $"No entity found for {entityId1}";

            var entity2 = manager.GetEntity<WorldEntity>(entityId2);
            if (entity2 == null) return $"No entity found for {entityId2}";

            Bounds bounds = entity1.Bounds;
            bool isBlocked = Region.IsBoundsBlockedByEntity(bounds, entity2, BlockingCheckFlags.CheckSpawns);
            return $"Entities\n [{entity1.PrototypeName}]\n [{entity2.PrototypeName}]\nIsBlocked: {isBlocked}";
        }

        [Command("tp")]
        [CommandDescription("Teleports self or another player to an entity matching the pattern. If teleporting another player, entity is searched in their region.")]
        [CommandUsage("entity tp [pattern] OR entity tp [targetPlayerName] [pattern]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)] // Minimum 1 parameter (pattern for self-teleport)
        public string Tp(string[] @params, NetClient client)
        {
            PlayerConnection adminConnection = (PlayerConnection)client;
            if (adminConnection == null) return "Error: Could not get admin player connection.";
            Player adminPlayer = adminConnection.Player;
            if (adminPlayer == null) return "Error: Could not get admin player entity.";
            Avatar adminAvatar = adminPlayer.CurrentAvatar;

            if (adminAvatar == null || !adminAvatar.IsInWorld)
            {
                return "Admin avatar not found or not in world.";
            }

            string entityPattern;
            Player playerToTeleport = adminPlayer; // Default to admin self-teleport
            Avatar avatarToTeleport = adminAvatar;
            Region searchRegion = adminAvatar.Region;

            if (@params.Length == 1) // Self-teleport: !entity tp [pattern]
            {
                entityPattern = @params[0];
            }
            else if (@params.Length >= 2) // Target player teleport: !entity tp [playerName] [pattern]
            {
                string targetPlayerName = @params[0];
                entityPattern = @params[1];

                // Find the target player
                var playerManager = ServerManager.Instance.GetGameService(ServerType.PlayerManager) as PlayerManagerService;
                if (playerManager == null) return "Error: PlayerManagerService is not available.";
                var groupingManager = ServerManager.Instance.GetGameService(ServerType.GroupingManager) as GroupingManagerService;
                if (groupingManager == null) return "Error: GroupingManagerService is not available.";

                if (!groupingManager.TryGetClient(targetPlayerName, out IFrontendClient targetFrontendClient))
                {
                    return $"Target player '{targetPlayerName}' not found or is not online.";
                }
                if (!(targetFrontendClient is FrontendClient targetGameClient))
                {
                    return $"Target player '{targetPlayerName}' is not a recognized game client type.";
                }
                if (targetGameClient.GameId == 0)
                {
                    return $"Target player '{targetPlayerName}' is not currently associated with a game world.";
                }
                Game targetPlayerGame = playerManager.GetGameByPlayer(targetGameClient);
                if (targetPlayerGame == null)
                {
                    return $"Could not find the game instance for target player '{targetPlayerName}'.";
                }
                if (targetGameClient.Session == null || targetGameClient.Session.Account == null)
                {
                    return $"Target player '{targetPlayerName}' session or account information is missing.";
                }
                ulong targetAccountDbId = (ulong)targetGameClient.Session.Account.Id;
                Player foundTargetPlayer = targetPlayerGame.EntityManager.IterateEntities().OfType<Player>().FirstOrDefault(p => p.DatabaseUniqueId == targetAccountDbId);

                if (foundTargetPlayer == null)
                {
                    return $"Could not find player entity for '{targetPlayerName}'.";
                }

                playerToTeleport = foundTargetPlayer;
                avatarToTeleport = playerToTeleport.CurrentAvatar;

                if (avatarToTeleport == null || !avatarToTeleport.IsInWorld)
                {
                    return $"Target player '{targetPlayerName}' does not have an active avatar in the world.";
                }
                searchRegion = avatarToTeleport.Region; // Search in the target player's region
            }
            else // Should be caught by CommandParamCount(1) but as a fallback
            {
                return "Invalid parameters. Usage: !entity tp [pattern] OR !entity tp [targetPlayerName] [pattern]";
            }

            if (searchRegion == null) return $"Region not found for {(playerToTeleport == adminPlayer ? "your" : playerToTeleport.GetName() + "'s")} avatar.";

            string patternLower = entityPattern.ToLowerInvariant();
            Entity targetEntity = searchRegion.Entities.FirstOrDefault(k =>
                GameDatabase.GetPrototypeName(k.PrototypeDataRef).ToLowerInvariant().Contains(patternLower));

            if (targetEntity == null) return $"No entity found with a prototype name containing '{entityPattern}' in {(playerToTeleport == adminPlayer ? "your" : playerToTeleport.GetName() + "'s")} current region.";

            if (targetEntity is not WorldEntity worldEntityToTpTo)
                return $"Found entity (Prototype: {GameDatabase.GetPrototypeName(targetEntity.PrototypeDataRef)}) is not a WorldEntity and cannot be teleported to.";

            Vector3 teleportPoint = worldEntityToTpTo.RegionLocation.Position;
            avatarToTeleport.ChangeRegionPosition(teleportPoint, null, ChangePositionFlags.Teleport);

            string teleportedPlayerName = playerToTeleport.GetName();
            string targetEntityName = GameDatabase.GetPrototypeName(worldEntityToTpTo.PrototypeDataRef);

            if (playerToTeleport == adminPlayer)
            {
                return $"Teleporting you to {targetEntityName} (ID: {worldEntityToTpTo.Id}) at {teleportPoint.ToStringNames()}.";
            }
            else
            {
                // Notify the target player if they were teleported by an admin
                if (playerToTeleport.PlayerConnection?.FrontendClient is FrontendClient tc)
                {
                    ChatHelper.SendMetagameMessage(tc, $"You have been teleported by an admin to {targetEntityName}.");
                }
                return $"Teleported player '{teleportedPlayerName}' to {targetEntityName} (ID: {worldEntityToTpTo.Id}) at {teleportPoint.ToStringNames()}.";
            }
        }

        [Command("create")]
        [CommandDescription("Create entity near the avatar based on pattern (ignore the case) and count (default 1).")]
        [CommandUsage("entity create [pattern] [count]")]
        [CommandUserLevel(AccountUserLevel.Moderator)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Create(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Game game = playerConnection.Game;

            if (game == null)
                return "Game not found.";

            Avatar avatar = playerConnection.Player.CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false)
                return "Avatar not found.";

            Region region = avatar.Region;
            if (region == null) return "No region found.";

            PrototypeId agentRef = CommandHelper.FindPrototype(HardcodedBlueprints.Agent, @params[0], client);
            if (agentRef == PrototypeId.Invalid) return string.Empty;

            var agentProto = GameDatabase.GetPrototype<AgentPrototype>(agentRef);

            int count = 1;
            if (@params.Length == 2)
                int.TryParse(@params[1], out count);

            for (int i = 0; i < count; i++)
            {
                if (EntityHelper.GetSpawnPositionNearAvatar(avatar, region, agentProto.Bounds, 250, out Vector3 positon) == false)
                    return "No space found to spawn the entity";
                var orientation = Orientation.FromDeltaVector(avatar.RegionLocation.Position - positon);
                EntityHelper.CreateAgent(agentProto, avatar, positon, orientation);
            }

            return $"Created!";
        }
    }
}
