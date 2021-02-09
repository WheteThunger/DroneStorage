using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Drone Storage", "WhiteThunder", "1.0.0")]
    [Description("Adds a small stash to deployed drones.")]
    internal class DroneStorage : CovalencePlugin
    {
        #region Fields

        private static DroneStorage _pluginInstance;

        private const string PermissionDropItems = "dronestorage.dropitems";
        private const string PermissionViewItems = "dronestorage.viewitems";
        private const string PermissionCapacityPrefix = "dronestorage.capacity";

        private const string StashPrefab = "assets/prefabs/deployable/small stash/small_stash_deployed.prefab";
        private const string AutoTurretPrefab = "assets/prefabs/npc/autoturret/autoturret_deployed.prefab";
        private const string StashDeployEffectPrefab = "assets/prefabs/deployable/small stash/effects/small-stash-deploy.prefab";
        private const string DropBagPrefab = "assets/prefabs/misc/item drop/item_drop.prefab";

        private const string MaximumCapacityPanelName = "genericlarge";
        private const int MaximumCapacity = 42;

        private static readonly Dictionary<string, int> DisplayCapacityByPanelName = new Dictionary<string, int>
        {
            ["fuelsmall"] = 1,
            ["smallstash"] = 6,
            ["smallwoodbox"] = 12,
            ["largewoodbox"] = 30,
            ["generic"] = 36,
            [MaximumCapacityPanelName] = MaximumCapacity,
        };

        private static readonly Vector3 StashLocalPosition = new Vector3(0, 0.24f, 0);
        private static readonly Vector3 StashDropForwardLocation = new Vector3(0, 0, 0.7f);

        private readonly Dictionary<Drone, ComputerStation> _controlledDrones = new Dictionary<Drone, ComputerStation>();

        // TODO: Remove this when we can use a post-hook variant of OnBookmarkControlEnd.
        private readonly HashSet<BasePlayer> _droneStashLooters = new HashSet<BasePlayer>();

        private Configuration _pluginConfig;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;

            permission.RegisterPermission(PermissionDropItems, this);
            permission.RegisterPermission(PermissionViewItems, this);

            foreach (var capacityAmount in _pluginConfig.CapacityAmountsRequiringPermission)
                permission.RegisterPermission(GetCapacityPermission(capacityAmount), this);

            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            UI.DestroyAll();

            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null || drone is DeliveryDrone)
                    continue;

                AddOrUpdateStash(drone);
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (drone is DeliveryDrone)
                return;

            TrySpawnStash(drone);
        }

        private void OnEntityDeath(Drone drone)
        {
            if (drone is DeliveryDrone)
                return;

            var stash = GetChildOfType<StashContainer>(drone);
            if (stash != null)
                DropItems(drone, stash);
        }

        private void OnEntityKill(Drone drone)
        {
            if (drone is DeliveryDrone)
                return;

            ComputerStation computerStation;
            if (!_controlledDrones.TryGetValue(drone, out computerStation))
                return;

            var controllerPlayer = computerStation.GetMounted();
            if (controllerPlayer == null)
                return;

            _controlledDrones.Remove(drone);
            UI.Destroy(controllerPlayer);
        }

        private object CanHideStash(BasePlayer player, StashContainer stash)
        {
            if (GetParentDrone(stash) != null)
                return false;

            return null;
        }

        private object OnEntityTakeDamage(StashContainer stash, HitInfo info)
        {
            var drone = GetParentDrone(stash);
            if (drone == null)
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return true;
        }

        private void OnBookmarkControl(ComputerStation computerStation, BasePlayer player, string bookmarkName, IRemoteControllable entity)
        {
            CleanupCache(computerStation);
            UI.Destroy(player);

            // TODO: Narrow hook signature after updating to use a post-hook variant of OnBookmarkControlEnd.
            var drone = entity as Drone;
            if (drone == null)
                return;

            // Without a delay, we can't know whether another plugin blocked the entity from being controlled.
            NextTick(() =>
            {
                if (computerStation == null
                    || computerStation.currentlyControllingEnt.uid != drone.net.ID
                    || computerStation._mounted != player)
                    return;

                _controlledDrones[drone] = computerStation;
                UI.Create(player);
            });
        }

        private void OnBookmarkControlEnd(ComputerStation station, BasePlayer player, Drone drone)
        {
            _controlledDrones.Remove(drone);
            UI.Destroy(player);
        }

        // TODO: Remove this when we can use a post-hook variant of OnBookmarkControlEnd.
        private void OnEntityDismounted(ComputerStation station, BasePlayer player)
        {
            DisconnectLooter(player);
            CleanupCache(station);

            if (player != null)
                UI.Destroy(player);
        }

        // TODO: Remove this when we can use a post-hook variant of OnBookmarkControlEnd.
        private void OnLootEntityEnd(BasePlayer player, StashContainer stash) =>
            _droneStashLooters.Remove(player);

        #endregion

        #region Commands

        [Command("dronestorage.ui.dropitems")]
        private void UICommandDropItems(IPlayer player)
        {
            var basePlayer = player.Object as BasePlayer;
            var computerStation = basePlayer.GetMounted() as ComputerStation;
            if (computerStation == null)
                return;

            var drone = GetControlledDrone(computerStation);
            if (drone == null)
                return;

            var stash = GetChildStash(drone);
            if (stash == null)
                return;

            if (!player.HasPermission(PermissionDropItems))
                return;

            DropItems(drone, stash, basePlayer);
        }

        [Command("dronestorage.ui.viewitems")]
        private void UICommandViewItems(IPlayer player)
        {
            var basePlayer = player.Object as BasePlayer;
            var computerStation = basePlayer.GetMounted() as ComputerStation;
            if (computerStation == null)
                return;

            var drone = GetControlledDrone(computerStation);
            if (drone == null)
                return;

            var stash = GetChildStash(drone);
            if (stash == null)
                return;

            if (!player.HasPermission(PermissionViewItems))
                return;

            if (basePlayer.inventory.loot.IsLooting() && basePlayer.inventory.loot.entitySource == stash)
            {
                // HACK: Send empty respawn information to fully close the player inventory (close the storage)
                basePlayer.ClientRPCPlayer(null, basePlayer, "OnRespawnInformation");
                return;
            }

            stash.PlayerOpenLoot(basePlayer, stash.panelName, doPositionChecks: false);
            _droneStashLooters.Add(basePlayer);
        }

        #endregion

        #region UI

        private static class UI
        {
            private const string Name = "DroneStorage";

            private const int ButtonWidth = 65;
            private const int ButtonHeight = 22;

            private const int ButtonSpacing = 10;
            private const int NumButtons = 2;

            private const int OffsetTop = 74;

            private static float GetButtonOffsetX(int index, int totalButtons)
            {
                var panelWidth = ButtonWidth * totalButtons + ButtonSpacing * (totalButtons - 1);
                var offsetXMin = -panelWidth / 2 + (ButtonWidth + ButtonSpacing) * index;
                return offsetXMin;
            }

            public static void Create(BasePlayer player)
            {
                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                // The computer station UI is inconsistent across resolutions.
                                // Positioning relative to top center for best approximate fit.
                                AnchorMin = "0.5 1",
                                AnchorMax = "0.5 1",
                                OffsetMin = $"0 {-OffsetTop - ButtonHeight}",
                                OffsetMax = $"0 {-OffsetTop}"
                            }
                        },
                        "Overlay",
                        Name
                    }
                };

                var iPlayer = player.IPlayer;
                var showViewItemsButton = iPlayer.HasPermission(PermissionViewItems);
                var showDropItemsButton = iPlayer.HasPermission(PermissionDropItems);

                var totalButtons = Convert.ToInt32(showViewItemsButton) + Convert.ToInt32(showDropItemsButton);
                var currentButtonIndex = 0;

                if (showViewItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(currentButtonIndex++, totalButtons);
                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = _pluginInstance.GetMessage(player.UserIDString, "UI.Button.ViewItems"),
                                Align = TextAnchor.MiddleCenter,
                                Color = "0 0 0 1",
                                FontSize = 12
                            },
                            Button =
                            {
                                Color = "0 1 0 1",
                                Command = "dronestorage.ui.viewitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin} 0",
                                OffsetMax = $"{offsetXMin + ButtonWidth} {ButtonHeight}"
                            }
                        },
                        Name
                    );
                }

                if (showDropItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(currentButtonIndex++, totalButtons);
                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = _pluginInstance.GetMessage(player.UserIDString, "UI.Button.DropItems"),
                                Align = TextAnchor.MiddleCenter,
                                Color = "0 0 0 1",
                                FontSize = 12
                            },
                            Button =
                            {
                                Color = "1 0 0 1",
                                Command = "dronestorage.ui.dropitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin} 0",
                                OffsetMax = $"{offsetXMin + ButtonWidth} {ButtonHeight}"
                            }
                        },
                        Name
                    );
                }

                CuiHelper.AddUi(player, cuiElements);
            }

            public static void Destroy(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }

            public static void DestroyAll()
            {
                foreach (var player in BasePlayer.activePlayerList)
                    Destroy(player);
            }
        }

        #endregion

        #region Helper Methods

        private static bool SpawnStorageWasBlocked(Drone drone)
        {
            object hookResult = Interface.CallHook("OnDroneStorageSpawn", drone);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool DropStorageWasBlocked(Drone drone, StashContainer stash, BasePlayer pilot)
        {
            object hookResult = Interface.CallHook("OnDroneStorageDrop", drone, stash, pilot);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static string GetCapacityPermission(int capacity) =>
            $"{PermissionCapacityPrefix}.{capacity}";

        private static Drone GetParentDrone(BaseEntity entity) =>
            entity.GetParentEntity() as Drone;

        private static Drone GetControlledDrone(ComputerStation computerStation) =>
            computerStation.currentlyControllingEnt.Get(serverside: true) as Drone;

        private static StashContainer GetChildStash(Drone drone) =>
            GetChildOfType<StashContainer>(drone);

        private static T GetChildOfType<T>(BaseEntity entity) where T : BaseEntity
        {
            foreach (var child in entity.children)
            {
                var childOfType = child as T;
                if (childOfType != null)
                    return childOfType;
            }
            return null;
        }

        private static void HitNotify(BaseEntity entity, HitInfo info)
        {
            var player = info.Initiator as BasePlayer;
            if (player == null)
                return;

            entity.ClientRPCPlayer(null, player, "HitNotify");
        }

        private StashContainer AddStashContainer(Drone drone, int capacity)
        {
            var stash = GameManager.server.CreateEntity(StashPrefab, StashLocalPosition) as StashContainer;
            if (stash == null)
                return null;

            // Damage will be processed by the drone.
            stash.baseProtection = null;

            stash.SetParent(drone);
            stash.Spawn();

            stash.inventory.capacity = capacity;
            stash.panelName = GetSmallestPanelForCapacity(capacity);

            Effect.server.Run(StashDeployEffectPrefab, stash.transform.position);
            Interface.CallHook("OnDroneStorageSpawned", drone, stash);

            return stash;
        }

        private static string GetSmallestPanelForCapacity(int capacity)
        {
            string panelName = MaximumCapacityPanelName;
            int displayCapacity = MaximumCapacity;

            foreach (var entry in DisplayCapacityByPanelName)
            {
                if (entry.Value >= capacity && entry.Value < displayCapacity)
                {
                    panelName = entry.Key;
                    displayCapacity = entry.Value;
                }
            }

            return panelName;
        }

        private static void RemoveProblemComponents(BaseEntity ent)
        {
            foreach (var meshCollider in ent.GetComponentsInChildren<MeshCollider>())
                UnityEngine.Object.DestroyImmediate(meshCollider);

            UnityEngine.Object.DestroyImmediate(ent.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(ent.GetComponent<GroundWatch>());
        }

        private static void DropItems(Drone drone, StashContainer stash, BasePlayer pilot = null)
        {
            var itemList = stash.inventory.itemList;
            if (itemList == null || itemList.Count <= 0 || stash.dropChance == 0)
                return;

            if (DropStorageWasBlocked(drone, stash, pilot))
                return;

            var dropPosition = pilot == null
                ? drone.transform.position
                : drone.transform.TransformPoint(StashDropForwardLocation);

            Effect.server.Run(StashDeployEffectPrefab, stash.transform.position);
            var dropContainer = stash.inventory.Drop(DropBagPrefab, dropPosition, stash.transform.rotation);
            Interface.Call("OnDroneStorageDropped", drone, stash, dropContainer, pilot);
        }

        // TODO: Remove this when we can use a post-hook variant of OnBookmarkControlEnd.
        private void DisconnectLooter(BasePlayer player)
        {
            if (_droneStashLooters.Contains(player))
                player.inventory.loot.Clear();
        }

        // This fixes an issue where switching from a drone to a camera doesn't remove the UI.
        // TODO: Remove this when we can use a post-hook variant of OnBookmarkControlEnd.
        private void CleanupCache(ComputerStation station)
        {
            var drone = GetCachedControlledDrone(station);
            if (drone != null)
                _controlledDrones.Remove(drone);
        }

        private Drone GetCachedControlledDrone(ComputerStation station)
        {
            foreach (var entry in _controlledDrones)
            {
                if (entry.Value == station)
                    return entry.Key;
            }
            return null;
        }

        private void AddOrUpdateStash(Drone drone)
        {
            var stash = GetChildStash(drone);
            if (stash != null)
            {
                // Possibly increase capacity, but do not decrease it as it could hide items.
                stash.inventory.capacity = Math.Max(stash.inventory.capacity, GetPlayerAllowedCapacity(drone.OwnerID));
                stash.panelName = GetSmallestPanelForCapacity(stash.inventory.capacity);
                return;
            }
            TrySpawnStash(drone);
        }

        private void TrySpawnStash(Drone drone)
        {
            var capacity = GetPlayerAllowedCapacity(drone.OwnerID);
            if (capacity <= 0)
                return;

            if (SpawnStorageWasBlocked(drone))
                return;

            AddStashContainer(drone, capacity);
        }

        #endregion

        #region Configuration

        private int GetPlayerAllowedCapacity(ulong ownerId)
        {
            var capacityAmounts = _pluginConfig.CapacityAmountsRequiringPermission;

            if (ownerId == 0 || capacityAmounts == null)
                return _pluginConfig.DefaultCapacity;

            var ownerIdString = ownerId.ToString();

            for (var i = capacityAmounts.Length - 1; i >= 0; i--)
            {
                var capacity = capacityAmounts[i];
                if (permission.UserHasPermission(ownerIdString, GetCapacityPermission(capacity)))
                    return capacity;
            }

            return _pluginConfig.DefaultCapacity;
        }

        internal class Configuration : SerializableConfiguration
        {
            [JsonProperty("DefaultCapacity")]
            public int DefaultCapacity = 0;

            [JsonProperty("CapacityAmountsRequiringPermission")]
            public int[] CapacityAmountsRequiringPermission = new int[] { 6, 18, 30, 42 };
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        internal class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        internal static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UI.Button.ViewItems"] = "View Items",
                ["UI.Button.DropItems"] = "Drop Items",
            }, this, "en");
        }

        #endregion
    }
}
